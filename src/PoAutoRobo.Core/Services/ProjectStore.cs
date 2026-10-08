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
    public static Episode Load(string folder) => Read(Path.Combine(folder, FileName));

    public static Episode RestoreBackup(string folder)
    {
        var path = Path.Combine(folder, FileName);
        var episode = Read(path + ".bak");
        File.Copy(path + ".bak", path, overwrite: true);
        return episode;
    }

    /// <summary>Episode folders directly under <paramref name="root"/>, most recently saved first.</summary>
    public static IReadOnlyList<string> ListEpisodes(string root) =>
        Directory.Exists(root)
            ? [.. Directory.GetDirectories(root)
                .Where(folder => File.Exists(Path.Combine(folder, FileName)))
                .OrderByDescending(folder => File.GetLastWriteTimeUtc(Path.Combine(folder, FileName)))]
            : [];

    private static Episode Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Episode>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"{path} is empty.");
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            throw new InvalidDataException($"{path} is missing or damaged.", e);
        }
    }
}
