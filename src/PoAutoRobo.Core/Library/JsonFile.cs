using System.Text.Json;

namespace PoAutoRobo.Core.Library;

/// <summary>Small files of remembered choices. Losing one is never worth interrupting the work for.</summary>
public static class JsonFile
{
    /// <summary>The saved value, or <paramref name="fallback"/> when the file is absent or unreadable.</summary>
    public static T Load<T>(string path, T fallback, JsonSerializerOptions? options = null)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options) ?? fallback;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return fallback;
        }
    }

    public static void Save<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        try
        {
            Files.EnsureFolderFor(path);
            File.WriteAllText(path, JsonSerializer.Serialize(value, options));
        }
        catch (IOException)
        {
            // A remembered preference is a convenience; failing to save one must not interrupt the work.
        }
    }
}
