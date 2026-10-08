
namespace PoAutoRobo.App.ViewModels;

/// <summary>The few choices the app remembers between runs.</summary>
public sealed record Prefs(int LengthIndex = 0, int ExportPresetIndex = 0, bool SoundsOn = true, int SubjectIndex = 0, double SoundVolume = 0.6, bool DraftPictures = false)
{
    public static Prefs Load() => JsonFile.Load(AppPaths.Prefs, new Prefs());

    public void Save() => JsonFile.Save(AppPaths.Prefs, this);
}
