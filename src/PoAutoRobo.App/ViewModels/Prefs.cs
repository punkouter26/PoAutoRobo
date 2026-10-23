
namespace PoAutoRobo.App.ViewModels;

/// <summary>The few choices the app remembers between runs, and where the user was when it closed.</summary>
/// <param name="LastEpisodeFolder">The episode that was open, reopened on the next launch; null when none was.</param>
/// <param name="Page">The step that episode was on; -1 when saved by a build that had three steps, whose numbers meant other pages.</param>
/// <param name="MonthlyBudget">The most to spend in a calendar month, in dollars; 0 for no limit.</param>
/// <param name="Effects">The decorative light and motion; off until turned on in Settings.</param>
/// <param name="Advanced">The controls most episodes never need; hidden until turned on in Settings.</param>
public sealed record Prefs(
    int ExportPresetIndex = 0, bool SoundsOn = true, double SoundVolume = 0.6, bool DraftPictures = false,
    string? LastEpisodeFolder = null, int Page = -1, int InspectorTab = 0, double MonthlyBudget = 0, int StructureIndex = 0,
    bool Effects = false, bool Advanced = false)
{
    public static Prefs Load() => JsonFile.Load(AppPaths.Prefs, new Prefs());

    public void Save() => JsonFile.Save(AppPaths.Prefs, this);
}
