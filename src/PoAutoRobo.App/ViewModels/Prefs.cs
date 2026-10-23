
namespace PoAutoRobo.App.ViewModels;

/// <summary>The few choices the app remembers between runs, and where the user was when it closed.</summary>
/// <param name="LastEpisodeFolder">The episode that was open, reopened on the next launch; null when none was.</param>
/// <param name="Page">The step that episode was on.</param>
/// <param name="MonthlyBudget">The most to spend in a calendar month, in dollars; 0 for no limit.</param>
/// <param name="Advanced">The controls most episodes never need; hidden until turned on in Settings.</param>
public sealed record Prefs(
    int ExportPresetIndex = 0, bool DraftPictures = false, string? LastEpisodeFolder = null, int Page = 0, int InspectorTab = 0,
    double MonthlyBudget = 0, bool Advanced = false)
{
    public static Prefs Load() => JsonFile.Load(AppPaths.Prefs, new Prefs());

    public void Save() => JsonFile.Save(AppPaths.Prefs, this);
}
