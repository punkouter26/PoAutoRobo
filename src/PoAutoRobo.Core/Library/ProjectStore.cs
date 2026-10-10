using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoAutoRobo.Core.Library;

/// <summary>One saved episode as the library lists it.</summary>
/// <param name="Thumbnail">The first generated picture still on disk, or null.</param>
public sealed record EpisodeSummary(string Folder, string Title, string? Thumbnail, int Clips, TimeSpan Runtime, DateTime Saved)
{
    public string Details => Clips == 0 ? "Damaged · open to restore" : $"{(Clips == 1 ? "1 clip" : $"{Clips} clips")} · {Runtime:m\\:ss}";

    /// <summary>The full title and when it was last saved, for a tooltip; the list itself has room for neither.</summary>
    public string Tip => $"{Title}\nSaved {Saved:d MMM yyyy, HH:mm}";

    public override string ToString() => Title; // what a screen reader calls the item
}

public static class ProjectStore
{
    public const string FileName = "episode.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // A saved episode with a field missing or null fails to read, and is reported as damaged.
    private static readonly JsonSerializerOptions Strict = new(JsonOptions)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static void Save(Episode episode, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(episode, JsonOptions));
        if (File.Exists(path))
            File.Replace(temp, path, path + ".bak");
        else
            File.Move(temp, path);
    }

    /// <exception cref="InvalidDataException">The file is missing or unreadable; offer <see cref="RestoreBackup"/>.</exception>
    public static Episode Load(string folder) => Read(Path.Combine(folder, FileName), folder);

    public static Episode RestoreBackup(string folder)
    {
        var path = Path.Combine(folder, FileName);
        var episode = Read(path + ".bak", folder);
        File.Copy(path + ".bak", path, overwrite: true);
        return episode;
    }

    // An episode file can be edited by anyone who can reach the (often cloud-synced) folder, so it is not trusted.
    // The serializer refuses missing and null fields; what it cannot see is checked here, where a problem can be
    // reported as a damaged file and not left to crash later.
    private static void CheckShape(Episode episode, string path)
    {
        var sound = AssCaptions.IsHexColour(episode.Captions.AccentColor)
            // A clip always has the depth it is on; the other depths are written on demand and may be absent.
            && episode.Clips.All(c => c is not null && c.Scripts.ContainsKey(c.ActiveTier) && c.Scripts.Values.All(script => script is not null)
                && (c.Visual.EarlierTakes ?? []).All(take => take is not null && take.All(path => path is not null)));
        if (!sound)
            throw new InvalidDataException($"{path} is missing or damaged.");
    }

    // Media must live inside the episode's own folder. A path pointing anywhere else (a network share, a system
    // file) is dropped, so opening an episode can never make the app reach out to or read from another place.
    private static Episode KeepOwnMedia(Episode episode, string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        bool Inside(string path) => Path.GetFullPath(path, root).StartsWith(root, StringComparison.OrdinalIgnoreCase);

        return episode with
        {
            MusicPath = episode.MusicPath is { } music && Inside(music) ? music : null,
            Clips = [.. episode.Clips.Select(clip =>
            {
                var visual = clip.Visual;
                if (visual.UserVideoPath is { } video && !Inside(video))
                    return clip with { NarrationRate = 1.0, Visual = new VisualSpec(VisualKind.TitleCard) };
                if (visual.EarlierTakes is { } takes && !takes.All(take => take.All(Inside)))
                    visual = visual with { EarlierTakes = [.. takes.Where(take => take.All(Inside))] };
                if (visual.MediaPaths is { } media && !media.All(Inside))
                    visual = visual with { MediaPaths = [.. media.Where(Inside)] };
                return ReferenceEquals(visual, clip.Visual) ? clip : clip with { Visual = visual };
            })],
        };
    }

    /// <summary>Episode folders directly under <paramref name="root"/>, most recently saved first.</summary>
    public static IReadOnlyList<string> ListEpisodes(string root) =>
        Directory.Exists(root)
            ? [.. Directory.GetDirectories(root)
                .Where(folder => File.Exists(Path.Combine(folder, FileName)))
                .OrderByDescending(folder => File.GetLastWriteTimeUtc(Path.Combine(folder, FileName)))]
            : [];

    /// <summary>What the library shows for each saved episode. A damaged one is still listed, so it can be opened and restored.</summary>
    public static IReadOnlyList<EpisodeSummary> Summaries(string root) =>
        [.. ListEpisodes(root).Select(folder =>
        {
            var saved = File.GetLastWriteTime(Path.Combine(folder, FileName));
            try
            {
                var episode = Load(folder);
                return new EpisodeSummary(folder, episode.Title, episode.Cover(), episode.Clips.Count, Durations.Total(episode), saved);
            }
            catch (InvalidDataException)
            {
                return new EpisodeSummary(folder, Path.GetFileName(folder), null, 0, TimeSpan.Zero, saved);
            }
        })];

    private const string SnippetsFile = "grounding.json";

    /// <summary>Keeps the passages a script was written from beside the episode, so they can be shown and checked against later.</summary>
    public static void SaveSnippets(IReadOnlyList<GroundingSnippet> snippets, string folder) =>
        File.WriteAllText(Path.Combine(folder, SnippetsFile), JsonSerializer.Serialize(snippets, JsonOptions));

    /// <summary>The saved passages with web links only; none when the file is absent or unreadable.</summary>
    public static IReadOnlyList<GroundingSnippet> LoadSnippets(string folder)
    {
        try
        {
            var snippets = JsonSerializer.Deserialize<List<GroundingSnippet>>(File.ReadAllText(Path.Combine(folder, SnippetsFile)), JsonOptions) ?? [];
            return [.. snippets.Where(s => s is { Repo: not null, Path: not null, Text: not null } && TrendFeed.IsWebLink(s.Url ?? ""))];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Copies an episode to a new folder beside it, without its finished videos, and returns that folder.</summary>
    public static string Duplicate(string folder, string root)
    {
        var episode = Load(folder);
        var title = episode.Title + " copy";
        var copy = NewFolder(root, title);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file);
            if (relative.StartsWith("export" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(copy, relative))!);
            File.Copy(file, Path.Combine(copy, relative));
        }

        // Media is recorded by full path, so the copy's clips must point at the copy's own files.
        string Moved(string path) => Path.Combine(copy, Path.GetRelativePath(folder, Path.GetFullPath(path, folder)));
        Save(episode with
        {
            Title = title,
            MusicPath = episode.MusicPath is { } music ? Moved(music) : null,
            Clips = [.. episode.Clips.Select(c => c with
            {
                Visual = c.Visual with
                {
                    UserVideoPath = c.Visual.UserVideoPath is { } video ? Moved(video) : null,
                    MediaPaths = c.Visual.MediaPaths is { } media ? [.. media.Select(Moved)] : null,
                    EarlierTakes = c.Visual.EarlierTakes is { } takes ? [.. takes.Select(take => (IReadOnlyList<string>)[.. take.Select(Moved)])] : null,
                },
            })],
        }, copy);
        return copy;
    }

    private const int MaxFolderName = 60;

    // A file-system-safe name from a title: lower-case letters and digits joined by hyphens, capped in length.
    public static string Slug(string title)
    {
        var slug = System.Text.RegularExpressions.Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > MaxFolderName - 4) // leaves room for a "-999" suffix
            slug = slug[..(MaxFolderName - 4)].Trim('-');
        return slug.Length == 0 ? "episode" : slug;
    }

    // A folder for a new episode that does not collide with one already saved under the same title.
    public static string NewFolder(string root, string title)
    {
        var slug = Slug(title);
        var folder = Path.Combine(root, slug);
        for (var n = 2; Directory.Exists(folder); n++)
            folder = Path.Combine(root, $"{slug}-{n}");
        return folder;
    }

    private static Episode Read(string path, string folder)
    {
        try
        {
            var episode = JsonSerializer.Deserialize<Episode>(File.ReadAllText(path), Strict)
                ?? throw new InvalidDataException($"{path} is empty.");
            CheckShape(episode, path);
            return KeepOwnMedia(episode, folder);
        }
        catch (Exception e) when (e is JsonException or IOException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"{path} is missing or damaged.", e);
        }
    }
}
