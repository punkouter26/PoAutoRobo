
namespace PoAutoRobo.App.ViewModels;

/// <summary>The few choices the app remembers between runs, and where the user was when it closed.</summary>
/// <param name="LastEpisodeFolder">The episode that was open, reopened on the next launch; null when none was.</param>
/// <param name="MonthlyBudget">The most to spend in a calendar month, in dollars; 0 for no limit.</param>
public sealed record Prefs(
    int LengthIndex = 0, int ExportPresetIndex = 0, bool SoundsOn = true, int SubjectIndex = 0, double SoundVolume = 0.6, bool DraftPictures = false,
    string? LastEpisodeFolder = null, int Step = 0, int InspectorTab = 0, double MonthlyBudget = 0)
{
    public static Prefs Load() => JsonFile.Load(AppPaths.Prefs, new Prefs());

    public void Save() => JsonFile.Save(AppPaths.Prefs, this);
}
