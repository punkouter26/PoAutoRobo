using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.AI.OpenAI;

namespace PoAutoRobo.Core.Script;

/// <summary>One file fetched from a repository, before it is cut into snippets.</summary>
public sealed record RepoDocument(string Repo, string Path, string Url, string Text);

/// <summary>Fetches the documents worth reading from one repository for these topic words.</summary>
public delegate Task<IReadOnlyList<RepoDocument>> RepoSource(string repo, IReadOnlyList<string> keywords, CancellationToken ct);

/// <summary>Turns each text into a list of numbers that sits close to the lists of texts meaning much the same.</summary>
public delegate Task<IReadOnlyList<float[]>> Embed(IReadOnlyList<string> texts, CancellationToken ct);

/// <summary>
/// Finds passages in the official repositories that bear on a topic, so the script can quote real names and numbers.
/// The <see cref="RepoSource"/> seam keeps ranking and caching testable without GitHub.
/// </summary>
public sealed partial class Grounding(RepoSource source, string cacheFolder)
{
    public const int MaxSnippetLength = 1500;
    private const int MaxSnippets = 8;

    /// <summary>How alike in meaning (0 to 1) a passage must be to the topic to be used when it shares none of its words.</summary>
    // ponytail: a round number, not a measured one. Lower it if reworded topics find too little; raise it if they find noise.
    private const double NearEnough = 0.5;

    /// <summary>Null when no embedding model is set up; passages are then found by matching words alone.</summary>
    public Embed? Embed { get; init; }

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

    /// <param name="expand">More words to search by, from the lesson (see <see cref="Expand"/>); null for its own words alone.</param>
    public static Grounding Create(string? gitHubToken, string cacheFolder, Embed? embed = null, Func<string, CancellationToken, Task<IReadOnlyList<string>>>? expand = null)
    {
        var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PoAutoRobo/1.0"); // GitHub refuses requests with no user agent
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        if (gitHubToken is not null)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", gitHubToken);

        async Task<JsonElement> Get(string path, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await http.GetStringAsync(path, ct));
            return json.RootElement.Clone();
        }

        // A file arrives as base64 text; one too large to send that way arrives empty and is left out.
        static string TextOf(JsonElement file) => Encoding.UTF8.GetString(Convert.FromBase64String(file.GetProperty("content").GetString() ?? ""));

        // Raw files are served from a different address with no rate limit worth the name, and need no sign-in.
        var raw = new HttpClient { BaseAddress = new Uri("https://raw.githubusercontent.com/"), Timeout = TimeSpan.FromSeconds(20) };
        var docsFolder = Path.Combine(cacheFolder, "docs");

        // Each repository's documentation pages whose path names the lesson's words. The list of pages is asked for
        // at most once a day, and each page once ever: its blob id names it, so a changed page is a new name.
        async Task<IReadOnlyList<RepoDocument>> DocsAsync(string repo, IReadOnlyList<string> keywords, CancellationToken ct)
        {
            var listFile = Path.Combine(docsFolder, repo.Replace('/', '_') + ".pages.json");
            List<DocPage> pages;
            if (File.Exists(listFile) && File.GetLastWriteTimeUtc(listFile) > DateTime.UtcNow - TimeSpan.FromDays(1))
            {
                pages = JsonSerializer.Deserialize<List<DocPage>>(await File.ReadAllTextAsync(listFile, ct)) ?? [];
            }
            else
            {
                var tree = await Get($"repos/{repo}/git/trees/HEAD?recursive=1", ct);
                pages = [.. tree.GetProperty("tree").EnumerateArray()
                    .Where(entry => entry.GetProperty("type").GetString() == "blob" && entry.TryGetProperty("size", out var size) && size.GetInt64() < MaxPageBytes)
                    .Select(entry => new DocPage(entry.GetProperty("path").GetString()!, entry.GetProperty("sha").GetString()!))
                    .Where(page => DocExtensions.Contains(Path.GetExtension(page.Path), StringComparer.OrdinalIgnoreCase)
                        && !NotDocs.Any(name => Path.GetFileName(page.Path).StartsWith(name, StringComparison.OrdinalIgnoreCase)))];
                Directory.CreateDirectory(docsFolder);
                await File.WriteAllTextAsync(listFile, JsonSerializer.Serialize(pages), ct);
            }

            var wanted = pages
                .Select(page => (page, hits: keywords.Count(k => page.Path.Contains(k, StringComparison.OrdinalIgnoreCase))))
                .Where(p => p.hits > 0)
                .OrderByDescending(p => p.hits).ThenBy(p => p.page.Path, StringComparer.Ordinal)
                .Take(DocsPerRepo)
                .Select(p => p.page);
            var documents = new List<RepoDocument>();
            foreach (var page in wanted)
            {
                var saved = Path.Combine(docsFolder, page.Sha + ".txt");
                var text = File.Exists(saved) ? await File.ReadAllTextAsync(saved, ct) : null;
                if (text is null)
                {
                    text = await raw.GetStringAsync($"{repo}/HEAD/{string.Join('/', page.Path.Split('/').Select(Uri.EscapeDataString))}", ct);
                    await File.WriteAllTextAsync(saved, text, ct);
                }
                // Markdown is cut at its headings later; any other page is cut to the part about the lesson now.
                var isMarkdown = page.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
                documents.Add(new RepoDocument(repo, page.Path, $"https://github.com/{repo}/blob/HEAD/{page.Path}", isMarkdown ? text : Around(text, keywords)));
            }
            return documents;
        }

        return new Grounding(async (repo, keywords, ct) =>
        {
            var readme = await Get($"repos/{repo}/readme", ct);
            var documents = new List<RepoDocument> { new(repo, readme.GetProperty("name").GetString()!, readme.GetProperty("html_url").GetString()!, TextOf(readme)) };

            try
            {
                documents.AddRange(await DocsAsync(repo, keywords, ct));
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or IOException)
            {
                // The pages are a help on top of the README; without them the README is still used.
            }

            // Code search needs a signed-in caller; without a token the READMEs alone are used.
            if (gitHubToken is not null && keywords.Count > 0)
                try
                {
                    var found = await Get($"search/code?per_page=3&q={Uri.EscapeDataString($"{string.Join(' ', keywords.Take(3))} repo:{repo}")}", ct);
                    foreach (var hit in found.GetProperty("items").EnumerateArray().Take(3))
                    {
                        var path = hit.GetProperty("path").GetString()!;
                        var file = await Get($"repos/{repo}/contents/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}", ct);
                        if (TextOf(file) is { Length: > 0 } text)
                            documents.Add(new RepoDocument(repo, path, hit.GetProperty("html_url").GetString()!, Around(text, keywords)));
                    }
                }
                catch (HttpRequestException)
                {
                    // Search has its own, tighter rate limit; keep the README we already have.
                }
            return documents;
        }, cacheFolder)
        { Embed = embed, Expand = expand };
    }

    /// <summary>An <see cref="Embed"/> that asks the Azure AI resource's embedding deployment.</summary>
    public static Embed AzureEmbedder(AppSettings settings)
    {
        var client = new AzureOpenAIClient(settings.Endpoint!, settings.Credential).GetEmbeddingClient(settings.EmbeddingDeployment);
        return async (texts, ct) => [.. (await client.GenerateEmbeddingsAsync(texts, cancellationToken: ct)).Value.Select(e => e.ToFloats().ToArray())];
    }

    /// <summary>Documentation pages read from each repository for one lesson, besides its README.</summary>
    private const int DocsPerRepo = 4;

    private const long MaxPageBytes = 100_000;
    private static readonly string[] DocExtensions = [".md", ".rst"];
    private static readonly string[] NotDocs = ["CHANGELOG", "LICENSE", "CODE_OF_CONDUCT", "CONTRIBUTING", "SECURITY"];

    private sealed record DocPage(string Path, string Sha);

    /// <summary>
    /// More words to search by, as they would be written in the code and the documents, from the lesson; null to
    /// search by the lesson's own words alone. When it fails the lesson's own words are used.
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<string>>>? Expand { get; init; }

    public async Task<IReadOnlyList<GroundingSnippet>> FindAsync(string topic, CancellationToken ct)
    {
        var keywords = Keywords(topic);
        if (Expand is { } expand)
            try
            {
                // The model's terms first: they are what the code is written in, and the search reads the first few.
                var terms = Keywords(string.Join(' ', await expand(topic, ct)));
                keywords = [.. terms, .. keywords.Except(terms)];
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // No model, no network: the lesson's own words still find passages, as they did before.
            }
        // The repositories are independent, so they are fetched together; results come back in repository order.
        var chunks = (await Task.WhenAll(OfficialRepos.Select(repo => FetchAsync(repo, keywords, ct)))).SelectMany(d => d).SelectMany(Chunk).ToList();
        var near = await NearnessAsync(topic, chunks, ct);

        // A passage earns its place by sharing the topic's words, by meaning the same in other words, or both. Meaning
        // counts for as much as every word matching, so a reworded topic still finds the passage that answers it.
        return [.. chunks
            .Select((snippet, position) => (snippet, position, hits: keywords.Count(k => snippet.Text.Contains(k, StringComparison.OrdinalIgnoreCase)), near: near[position]))
            .Where(s => s.hits > 0 || s.near >= NearEnough)
            .OrderByDescending(s => s.hits + keywords.Count * s.near)
            .ThenBy(s => s.position) // documents arrive in repository order, so this keeps ties deterministic
            .Take(MaxSnippets)
            .Select(s => s.snippet)];
    }

    private const string EmbeddingsFile = "embeddings.json";

    /// <summary>
    /// How alike in meaning each passage is to the topic, 0 to 1; all zeros when there is no embedding model or it
    /// fails. Each text's numbers are kept on disk by its content, so a passage is only ever sent once.
    /// </summary>
    private async Task<double[]> NearnessAsync(string topic, List<GroundingSnippet> chunks, CancellationToken ct)
    {
        var none = new double[chunks.Count];
        if (Embed is null || chunks.Count == 0)
            return none;
        try
        {
            var file = Path.Combine(cacheFolder, EmbeddingsFile);
            var known = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, float[]>>(await File.ReadAllTextAsync(file, ct)) ?? [] : [];
            static string Key(string text) => Files.TextHash(text)[..32];

            var fresh = chunks.Select(c => c.Text).Append(topic).Distinct().Where(text => !known.ContainsKey(Key(text))).ToList();
            if (fresh.Count > 0)
            {
                var made = await Embed(fresh, ct);
                for (var i = 0; i < fresh.Count; i++)
                    known[Key(fresh[i])] = made[i];
                Directory.CreateDirectory(cacheFolder);
                await File.WriteAllTextAsync(file, JsonSerializer.Serialize(known), ct);
            }
            var wanted = known[Key(topic)];
            return [.. chunks.Select(c => Math.Max(0, Cosine(wanted, known[Key(c.Text)])))];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // No such deployment, no network, a damaged file: matching words still works, as it did before.
            return none;
        }
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, lengthA = 0, lengthB = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            lengthA += a[i] * a[i];
            lengthB += b[i] * b[i];
        }
        return lengthA == 0 || lengthB == 0 ? 0 : dot / Math.Sqrt(lengthA * lengthB);
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
        [.. Durations.SplitWords(topic)
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

    /// <summary>
    /// Figures and code-style names in <paramref name="dialogue"/> that appear in none of the <paramref name="sources"/>:
    /// the things the script was told to take only from the repositories. A plain text check, no model involved.
    /// </summary>
    public static IReadOnlyList<string> UnverifiedClaims(string dialogue, IEnumerable<string> sources)
    {
        var known = string.Join('\n', sources);
        // Looked for a second time with the spaces taken out, so "50Hz" in the script is found in a source that writes "50 Hz".
        var packed = Spaces().Replace(known, "");
        return [.. Claim().Matches(dialogue).Select(m => m.Value).Distinct()
            .Where(claim => !known.Contains(claim, StringComparison.OrdinalIgnoreCase) && !packed.Contains(claim, StringComparison.OrdinalIgnoreCase))];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // A number of two or more digits or with a decimal point, with any unit joined to it; or a name with an underscore.
    [GeneratedRegex(@"\b\d+\.\d+\w*|\b\d{2,}\w*|\b[A-Za-z][A-Za-z0-9]*_\w+")]
    private static partial Regex Claim();

    /// <summary>The part of a source file around the first topic word, since the top of a file is usually licence text.</summary>
    private static string Around(string text, IReadOnlyList<string> keywords)
    {
        var hit = keywords.Select(k => text.IndexOf(k, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, hit - MaxSnippetLength / 3);
        return text.Substring(start, Math.Min(MaxSnippetLength, text.Length - start));
    }
}

/// <summary>A passage from an official repository that a script was written from.</summary>
public sealed record GroundingSnippet(string Repo, string Path, string Url, string Text);
