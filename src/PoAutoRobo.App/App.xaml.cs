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

    /// <summary>Owner handle for file dialogs.</summary>
    public static nint WindowHandle { get; private set; }

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // An unpackaged app has no app-data store, so WinUIEx needs somewhere to keep the window's size and position.
        var windowState = LoadWindowState();
        WindowManager.PersistenceStorage = windowState;

        // ponytail: the window appears after the vault answers (a second or two). Show it first if that ever feels slow.
        var settings = await LoadSettingsAsync();
        var plan = ServiceSelector.Plan(settings);
        IScriptWriter writer = plan.ScriptLive ? AzureScriptWriter.Create(settings) : new MockScriptWriter();
        INarrator narrator = plan.VoiceLive ? new AzureNarrator(settings) : new MockNarrator();
        var offline = plan.Simulated.Count == 0
            ? null
            : $"{string.Join(" and ", plan.Simulated)} simulated. {settings.LoadError ?? "The key vault has no key for your Azure AI resource."}";

        var grounding = Grounding.Create(settings.GitHubToken, Path.Combine(Path.GetDirectoryName(WindowStateFile)!, "grounding"));
        var ffmpeg = FfmpegRunner.Locate();
        var builder = new EpisodeBuilder(narrator, new FfmpegRunner(ffmpeg ?? "ffmpeg.exe"));
        _window = new MainWindow(new MainViewModel(writer, builder, grounding, ffmpegAvailable: ffmpeg is not null, offline));
        _window.Closed += (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WindowStateFile)!);
            File.WriteAllText(WindowStateFile, JsonSerializer.Serialize(windowState.ToDictionary(p => p.Key, p => p.Value?.ToString())));
        };
        WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _window.Activate();
    }

    private static async Task<AppSettings> LoadSettingsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            return await AppSettings.LoadAsync(new KeyVaultSecretSource(KeyVaultSecretSource.DefaultVault), timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return AppSettings.Offline with { LoadError = "The key vault did not answer in time." };
        }
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
