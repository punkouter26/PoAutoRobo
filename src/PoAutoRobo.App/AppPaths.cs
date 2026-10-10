namespace PoAutoRobo.App;

/// <summary>Where the app keeps things on this machine. The one place these folders are named.</summary>
public static class AppPaths
{
    /// <summary>Saved episodes, where the user can find them.</summary>
    public static string Episodes { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PoAutoRobo");

    /// <summary>The app's own files, which the user never needs to see.</summary>
    public static string Local { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoAutoRobo");

    public static string Prefs { get; } = Path.Combine(Local, "settings.json");

    public static string WindowState { get; } = Path.Combine(Local, "window.json");

    /// <summary>Host candidates made before any episode exists, and the locked host design.</summary>
    public static string Host { get; } = Path.Combine(Local, "host");

    public static string HostSheet { get; } = Path.Combine(Host, "sheet.png");

    public static string Grounding { get; } = Path.Combine(Local, "grounding");

    /// <summary>The names of AI videos still being made, so one that was stopped is picked up again and not paid for twice.</summary>
    public static string VideoJobs { get; } = Path.Combine(Local, "video-jobs");
}
