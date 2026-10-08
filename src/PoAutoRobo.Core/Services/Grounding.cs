using System.Text.Json;
using Octokit;

namespace PoAutoRobo.Core.Services;

/// <summary>One file fetched from a repository, before it is cut into snippets.</summary>
public sealed record RepoDocument(string Repo, string Path, string Url, string Text);

/// <summary>Fetches the documents worth reading from one repository for these topic words.</summary>
public delegate Task<IReadOnlyList<RepoDocument>> RepoSource(string repo, IReadOnlyList<string> keywords, CancellationToken ct);

/// <summary>
/// Finds passages in the official repositories that bear on a topic, so the script can quote real names and numbers.
/// The <see cref="RepoSource"/> seam keeps ranking and caching testable without GitHub.
/// </summary>
public sealed class Grounding(RepoSource source, string cacheFolder)
{
    public const int MaxSnippetLength = 1500;
    private const int MaxSnippets = 8;

    /// <summary>The only places facts may come from. Order breaks ranking ties.</summary>
    public static readonly IReadOnlyList<string> OfficialRepos =
    [
        "unitreerobotics/unitree_sdk2", "unitreerobotics/unitree_sdk2_python", "unitreerobotics/unitree_rl_lab",
        "unitreerobotics/unitree_rl_mjlab", "unitreerobotics/unitree_mujoco", "isaac-sim/IsaacLab", "google-deepmind/mujoco",
    ];

    private static readonly HashSet<string> Filler =
    [
        "a", "an", "the", "of", "on", "in", "to", "for", "and", "or", "with", "using", "use", "how", "what", "why", "is", "are", "its", "it", "my", "by", "from", "at",
    ];

    public static Grounding Create(string? gitHubToken, string cacheFolder)
    {
        var client = new GitHubClient(new ProductHeaderValue("PoAutoRobo"));
        if (gitHubToken is not null)
            client.Credentials = new Credentials(gitHubToken);

        return new Grounding(async (repo, keywords, ct) =>
        {
            var (owner, name) = (repo.Split('/')[0], repo.Split('/')[1]);
            var readme = await client.Repository.Content.GetReadme(owner, name);
            var documents = new List<RepoDocument> { new(repo, readme.Name, readme.HtmlUrl, readme.Content) };

            // Code search needs a signed-in caller; without a token the READMEs alone are used.
            if (gitHubToken is not null && keywords.Count > 0)
                try
                {
                    var request = new SearchCodeRequest(string.Join(' ', keywords.Take(3))) { Repos = [repo], PerPage = 3 };
                    foreach (var hit in (await client.Search.SearchCode(request)).Items.Take(3))
                    {
                        ct.ThrowIfCancellationRequested();
                        var file = (await client.Repository.Content.GetAllContents(owner, name, hit.Path))[0];
                        if (file.Content is { Length: > 0 } text)
                            documents.Add(new RepoDocument(repo, hit.Path, hit.HtmlUrl, Around(text, keywords)));
                    }
                }
                catch (ApiException)
                {
                    // Search has its own, tighter rate limit; keep the README we already have.
                }
            return documents;
        }, cacheFolder);
    }

    public async Task<IReadOnlyList<GroundingSnippet>> FindAsync(string topic, CancellationToken ct)
    {
        var keywords = Keywords(topic);
        var documents = new List<RepoDocument>();
        foreach (var repo in OfficialRepos)
            documents.AddRange(await FetchAsync(repo, keywords, ct));

        return [.. documents
            .SelectMany(Chunk)
            .Select((snippet, position) => (snippet, position, score: keywords.Count(k => snippet.Text.Contains(k, StringComparison.OrdinalIgnoreCase))))
            .Where(s => s.score > 0)
            .OrderByDescending(s => s.score)
            .ThenBy(s => s.position) // documents arrive in repository order, so this keeps ties deterministic
            .Take(MaxSnippets)
            .Select(s => s.snippet)];
    }

    /// <summary>Live copy when GitHub answers, otherwise the last good copy on disk, otherwise nothing.</summary>
    private async Task<IReadOnlyList<RepoDocument>> FetchAsync(string repo, IReadOnlyList<string> keywords, CancellationToken ct)
    {
        var cached = Path.Combine(cacheFolder, repo.Replace('/', '_') + ".json");
        try
        {
            var documents = await source(repo, keywords, ct);
            Directory.CreateDirectory(cacheFolder);
            await File.WriteAllTextAsync(cached, JsonSerializer.Serialize(documents), ct);
            return documents;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Rate limit, no network, repository moved: grounding is a help, never a reason to fail the episode.
            return File.Exists(cached) ? JsonSerializer.Deserialize<List<RepoDocument>>(await File.ReadAllTextAsync(cached, ct)) ?? [] : [];
        }
    }

    public static IReadOnlyList<string> Keywords(string topic) =>
        [.. topic.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', ':', ';', '!', '?', '(', ')', '"', '\'').ToLowerInvariant())
            .Where(w => w.Length >= 2 && !Filler.Contains(w))
            .Distinct()];

    /// <summary>Markdown is split at its headings; anything else is one snippet. Each is capped at <see cref="MaxSnippetLength"/>.</summary>
    public static IReadOnlyList<GroundingSnippet> Chunk(RepoDocument document)
    {
        var sections = new List<string>();
        if (document.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var current = new List<string>();
            foreach (var line in document.Text.ReplaceLineEndings("\n").Split('\n'))
            {
                if (line.StartsWith('#') && current.Count > 0)
                {
                    sections.Add(string.Join('\n', current));
                    current = [];
                }
                current.Add(line);
            }
            sections.Add(string.Join('\n', current));
        }
        else
        {
            sections.Add(document.Text);
        }
        return [.. sections
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => new GroundingSnippet(document.Repo, document.Path, document.Url, s.Length > MaxSnippetLength ? s[..MaxSnippetLength] : s))];
    }

    /// <summary>The part of a source file around the first topic word, since the top of a file is usually licence text.</summary>
    private static string Around(string text, IReadOnlyList<string> keywords)
    {
        var hit = keywords.Select(k => text.IndexOf(k, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, hit - MaxSnippetLength / 3);
        return text.Substring(start, Math.Min(MaxSnippetLength, text.Length - start));
    }
}
