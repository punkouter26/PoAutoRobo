using System.Reflection;
using Microsoft.UI.Xaml;
using PoAutoRobo.App.ViewModels;
using WinUIEx;

namespace PoAutoRobo.App;

public partial class App : Application
{
    private Window? _window;
    private bool _restarting;

    /// <summary>Owner handle for file dialogs.</summary>
    public static nint WindowHandle { get; private set; }

    /// <summary>Which build this is: the day of the last commit as YYMMDD and the commit, stamped in when the app was built.</summary>
    public static string BuildVersion { get; } = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BuildVersion")?.Value ?? "";

    public App() => InitializeComponent();

    /// <summary>
    /// Starts the app afresh, for a change that is read only as it starts. The window closes as if the user closed it,
    /// so any job stops and everything is saved first.
    /// </summary>
    public static void Restart()
    {
        if (Current is not App { _window: { } window } app) return;
        app._restarting = true;
        window.Close();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // An unpackaged app has no app-data store, so WinUIEx needs somewhere to keep the window's size and position.
        var windowState = JsonFile.Load(AppPaths.WindowState, new Dictionary<string, string>()).ToDictionary(p => p.Key, p => (object)p.Value);
        WindowManager.PersistenceStorage = windowState;

        // The window opens straight away, on stand-in services. The real ones are connected below once the vault
        // answers, which can take a second or two and up to twenty when there is no network.
        var ffmpeg = FfmpegRunner.Locate();
        var viewModel = new MainViewModel(ffmpegAvailable: ffmpeg is not null);
        _window = new MainWindow(viewModel);
        _window.Closed += (_, _) =>
        {
            JsonFile.Save(AppPaths.WindowState, windowState.ToDictionary(p => p.Key, p => p.Value?.ToString()));
            if (_restarting && Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe)?.Dispose();
        };
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

        var services = ServiceChoices.Load(AppPaths.Services);
        ServicesViewModel.StartedWith = services;
        var settings = (await LoadSettingsAsync()).Using(services);
        IScriptWriter writer = new MockScriptWriter();
        INarrator narrator = new MockNarrator();
        Func<string, Quality, Visuals>? visualsFor = null;
        Hear? hear = null;
        var runner = new FfmpegRunner(ffmpeg ?? "ffmpeg.exe");
        if (settings.IsLive)
        {
            var script = AzureScriptWriter.Create(settings);
            script.Used = viewModel.LogUsage;
            script.Note = viewModel.Note;
            writer = script;
            narrator = new AzureNarrator(settings) { Used = viewModel.LogUsage };
            hear = new AzureListener(settings) { Used = viewModel.LogUsage }.HearAsync;
            // The voice's own pace, as learned on earlier runs; the stand-in voice keeps the usual one.
            Durations.WordsPerMinute = JsonFile.Load(AppPaths.Pace, Durations.DefaultWordsPerMinute);
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
                Scene = scenes is null ? null : (request, output, ct) => DrawSceneAsync(script, scenes, runner, request, output, ct),
                Rethink = script.RethinkPictureAsync,
            };
            script.CanMake = visualsFor(AppPaths.Host, Quality.Medium).CanMake;
        }
        var paceGate = new Lock(); // recordings finish side by side
        viewModel.Connect(
            writer,
            new EpisodeBuilder(narrator, runner) { PaceLearned = settings.IsLive ? pace => { lock (paceGate) JsonFile.Save(AppPaths.Pace, pace); } : null },
            Grounding.Create(settings.GitHubToken, AppPaths.Grounding,
                settings is { IsLive: true, EmbeddingDeployment: not null } ? Grounding.AzureEmbedder(settings) : null,
                settings.IsLive ? writer.SearchTermsAsync : null),
            visualsFor,
            settings.ImageDeployment,
            settings.IsLive ? null : $"Script and voice simulated. {settings.LoadError ?? "The key vault has no address for your Azure AI resource."}",
            hear);
    }

    /// <summary>
    /// Has a scene written and drawn. Code that does not run is sent back once with what the browser said was wrong,
    /// which puts most of them right; a second failure is the user's to see. Code that runs can still draw a label off
    /// the frame or under the captions, so a frame of it once built is looked at, and a fault found is sent back once.
    /// </summary>
    private static async Task DrawSceneAsync(IScriptWriter script, SceneRenderer scenes, FfmpegRunner ffmpeg, SceneRequest request, string output, CancellationToken ct)
    {
        var scene = await script.WriteSceneAsync(request, ct);
        try
        {
            await scenes.RenderAsync(scene, request.Length, output, ct);
        }
        catch (SceneCodeException e)
        {
            await scenes.RenderAsync(await script.WriteSceneAsync(request, ct, scene, e.Detail), request.Length, output, ct);
            return; // already sent back once
        }

        var frame = Files.NewScratchFile(".png");
        var better = Files.NewScratchFile(".mp4");
        try
        {
            // Once it has built: it builds until about 85% of its length and then holds.
            await ffmpeg.RunAsync(FfmpegArgs.Still(output, request.Length * 0.9, 0, frame), Path.GetDirectoryName(frame)!, null, null, ct);
            if (await script.CheckSceneAsync(frame, ct) is { Fine: false } verdict)
            {
                await scenes.RenderAsync(await script.WriteSceneAsync(request, ct, scene, verdict.Problem), request.Length, better, ct);
                File.Move(better, output, overwrite: true);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The scene that was drawn runs: it is kept, faults and all, rather than nothing.
        }
        finally
        {
            File.Delete(frame);
            File.Delete(better);
        }
    }

    private static async Task<AppSettings> LoadSettingsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            return await AppSettings.LoadAsync(KeyVault.Default(), timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return AppSettings.Offline with { LoadError = "The key vault did not answer in time." };
        }
    }
}
