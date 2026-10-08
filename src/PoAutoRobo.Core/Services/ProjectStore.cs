using System.Text.Json;
using System.Text.Json.Serialization;
using PoAutoRobo.Core.Models;

namespace PoAutoRobo.Core.Services;

public static class ProjectStore
{
    public const string FileName = "episode.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
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
    // Its shape is checked here, where a problem can be reported as a damaged file, and not left to crash later.
    private static void CheckShape(Episode episode, string path)
    {
        var sound = episode.Clips is not null && episode.Captions is not null && episode.Mix is not null
            && Pipeline.AssCaptions.IsHexColour(episode.Captions.AccentColor)
            && episode.Clips.All(c => c is { Title: not null, Visual: not null, Scripts: not null }
                && Enum.GetValues<Tier>().All(t => c.Scripts.TryGetValue(t, out var script) && script is { Dialogue: not null, VisualPrompt: not null, Pose: not null }));
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
            Clips = [.. episode.Clips.Select(clip =>
            {
                var visual = clip.Visual;
                if (visual.UserVideoPath is { } video && !Inside(video))
                    return clip with { NarrationRate = 1.0, Visual = new VisualSpec(VisualKind.TitleCard) };
                if (visual.MediaPaths is { } media && !media.All(Inside))
                    return clip with { Visual = visual with { MediaPaths = [.. media.Where(Inside)] } };
                return clip;
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
            var episode = JsonSerializer.Deserialize<Episode>(File.ReadAllText(path), JsonOptions)
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
