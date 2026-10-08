using Microsoft.UI.Xaml;
using PoAutoRobo.App.ViewModels;
using WinUIEx;

namespace PoAutoRobo.App;

public partial class App : Application
{
    private Window? _window;

    /// <summary>Owner handle for file dialogs.</summary>
    public static nint WindowHandle { get; private set; }

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // An unpackaged app has no app-data store, so WinUIEx needs somewhere to keep the window's size and position.
        var windowState = JsonFile.Load(AppPaths.WindowState, new Dictionary<string, string>()).ToDictionary(p => p.Key, p => (object)p.Value);
        WindowManager.PersistenceStorage = windowState;

        // The window opens straight away, on stand-in services. The real ones are connected below once the vault
        // answers, which can take a second or two and up to twenty when there is no network.
        var ffmpeg = FfmpegRunner.Locate();
        var viewModel = new MainViewModel(TrendFeed.Create(), ffmpegAvailable: ffmpeg is not null);
        _window = new MainWindow(viewModel);
        _window.Closed += (_, _) => JsonFile.Save(AppPaths.WindowState, windowState.ToDictionary(p => p.Key, p => p.Value?.ToString()));
        _window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _window.Activate();
        // Whatever slips past the handlers around each action is shown, not left to close the app mid-episode.
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            viewModel.ErrorMessage = MainViewModel.Plain(e.Exception);
        };

        var settings = await LoadSettingsAsync();
        IScriptWriter writer = new MockScriptWriter();
        INarrator narrator = new MockNarrator();
        Func<string, string, Visuals>? visualsFor = null;
        if (settings.IsLive)
        {
            var script = AzureScriptWriter.Create(settings);
            script.Used = viewModel.LogUsage;
            writer = script;
            narrator = new AzureNarrator(settings) { Used = viewModel.LogUsage };
            var images = new AzureImageGen(settings, AzureImageGen.NewHttpClient());
            visualsFor = (folder, quality) => new Visuals(images, new MediaCache(Path.Combine(folder, "images")), AppPaths.HostSheet, settings.ImageDeployment)
            {
                Quality = quality,
                // Each picture really made is added to the episode's running cost, when its price is known.
                PictureMade = CostEstimate.PriceOf(settings.ImageDeployment, quality) is { } dollars ? () => SpendLog.Add(folder, "picture", dollars) : null,
            };
        }
        viewModel.Connect(
            writer,
            new EpisodeBuilder(narrator, new FfmpegRunner(ffmpeg ?? "ffmpeg.exe")),
            Grounding.Create(settings.GitHubToken, AppPaths.Grounding),
            visualsFor,
            settings.ImageDeployment,
            settings.IsLive ? null : $"Script and voice simulated. {settings.LoadError ?? "The key vault has no address for your Azure AI resource."}");
    }

    private static async Task<AppSettings> LoadSettingsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            return await AppSettings.LoadAsync(new KeyVaultSecretSource(KeyVaultSecretSource.DefaultVault, AppSettings.SignedInUser), timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return AppSettings.Offline with { LoadError = "The key vault did not answer in time." };
        }
    }
}
