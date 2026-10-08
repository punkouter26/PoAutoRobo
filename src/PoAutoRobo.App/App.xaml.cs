using System.Text.Json;
using Microsoft.UI.Xaml;
using PoAutoRobo.App.ViewModels;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;
using WinUIEx;

namespace PoAutoRobo.App;

public partial class App : Application
{
    private static readonly string WindowStateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoAutoRobo", "window.json");

    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // An unpackaged app has no app-data store, so WinUIEx needs somewhere to keep the window's size and position.
        var windowState = LoadWindowState();
        WindowManager.PersistenceStorage = windowState;

        _window = new MainWindow(new MainViewModel(new MockScriptWriter(), new EpisodeBuilder(new MockNarrator(), new FfmpegRunner(FfmpegRunner.Locate() ?? "ffmpeg.exe"))));
        _window.Closed += (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WindowStateFile)!);
            File.WriteAllText(WindowStateFile, JsonSerializer.Serialize(windowState.ToDictionary(p => p.Key, p => p.Value?.ToString())));
        };
        _window.Activate();
    }

    private static Dictionary<string, object> LoadWindowState()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(WindowStateFile)) ?? [];
            return saved.ToDictionary(p => p.Key, p => (object)p.Value);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return [];
        }
    }
}
