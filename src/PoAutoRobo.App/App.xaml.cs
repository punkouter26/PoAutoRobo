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
        // Working files a crash left behind. The finished clips kept between renders clear themselves by age.
        _ = Task.Run(() => Files.SweepScratch(TimeSpan.FromDays(1), EpisodeBuilder.ClipCacheName));

        var settings = await LoadSettingsAsync();
        IScriptWriter writer = new MockScriptWriter();
        INarrator narrator = new MockNarrator();
        Func<string, Quality, Visuals>? visualsFor = null;
        if (settings.IsLive)
        {
            var script = AzureScriptWriter.Create(settings);
            script.Used = viewModel.LogUsage;
            script.Note = viewModel.Note;
            writer = script;
            narrator = new AzureNarrator(settings) { Used = viewModel.LogUsage };
            var http = AzureImageGen.NewHttpClient();
            var images = new AzureImageGen(settings, http);
            // Each of these three is offered only when what it needs is there: a key, a named video model, Edge and FFmpeg.
            var stock = settings.PexelsKey is { } key ? new PexelsStock(key, http) : null;
            var video = settings.VideoDeployment is null ? null : new AzureVideoGen(settings, http, AppPaths.VideoJobs);
            var scenes = SceneRenderer.Locate() is { } edge && ffmpeg is not null ? new SceneRenderer(edge, new FfmpegRunner(ffmpeg)) : null;
            visualsFor = (folder, quality) => new Visuals(images.GenerateAsync, new MediaCache(Path.Combine(folder, "images")), AppPaths.HostSheet, settings.ImageDeployment)
            {
                Quality = quality,
                // Each picture really made is added to the episode's running cost, when its price is known.
                PictureMade = CostEstimate.PriceOf(settings.ImageDeployment, quality) is { } dollars ? () => SpendLog.Add(folder, SpendLog.Picture, dollars) : null,
                VideoMade = () => SpendLog.Add(folder, SpendLog.AiVideo, CostEstimate.VideoPrice),
                Stock = stock is null ? null : stock.FindAsync,
                Video = video is null ? null : video.GenerateAsync,
                Scene = scenes is null ? null : (request, output, ct) => DrawSceneAsync(script, scenes, request, output, ct),
                Rethink = script.RethinkPictureAsync,
            };
            script.CanMake = visualsFor(AppPaths.Host, Quality.Medium).CanMake;
        }
        viewModel.Connect(
            writer,
            new EpisodeBuilder(narrator, new FfmpegRunner(ffmpeg ?? "ffmpeg.exe")),
            Grounding.Create(settings.GitHubToken, AppPaths.Grounding, settings is { IsLive: true, EmbeddingDeployment: not null } ? Grounding.AzureEmbedder(settings) : null),
            visualsFor,
            settings.ImageDeployment,
            settings.IsLive ? null : $"Script and voice simulated. {settings.LoadError ?? "The key vault has no address for your Azure AI resource."}");
    }

    /// <summary>
    /// Has a scene written and drawn. Code that does not run is sent back once with what the browser said was wrong,
    /// which puts most of them right; a second failure is the user's to see.
    /// </summary>
    private static async Task DrawSceneAsync(IScriptWriter script, SceneRenderer scenes, SceneRequest request, string output, CancellationToken ct)
    {
        var scene = await script.WriteSceneAsync(request, ct);
        try
        {
            await scenes.RenderAsync(scene, request.Length, output, ct);
        }
        catch (SceneCodeException e)
        {
            await scenes.RenderAsync(await script.WriteSceneAsync(request, ct, scene, e.Detail), request.Length, output, ct);
        }
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
