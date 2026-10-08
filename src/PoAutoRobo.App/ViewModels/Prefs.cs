using System.Text.Json;

namespace PoAutoRobo.App.ViewModels;

/// <summary>The few choices the app remembers between runs.</summary>
public sealed record Prefs(int LengthIndex = 0, int ExportPresetIndex = 0, bool SoundsOn = true)
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoAutoRobo", "settings.json");

    public static Prefs Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Prefs>(File.ReadAllText(Path)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this));
        }
        catch (IOException)
        {
            // A remembered preference is a convenience; failing to save one must not interrupt the work.
        }
    }
}
