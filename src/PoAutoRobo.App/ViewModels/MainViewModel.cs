using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PoAutoRobo.App.ViewModels;

/// <summary>
/// The app's one view model. This file holds what it is connected to, what it remembers between runs and the open
/// episode with the line about it under the title; the other parts are
/// beside it: Steps, Library, Footage, Edits, Pictures, Series, Export, Teasers and Activity.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly bool _ready; // false while the constructor restores saved choices, so restoring them saves and fetches nothing

    // Simulated until Connect is called: the window opens before the Azure sign-in has answered.
    private IScriptWriter _scriptWriter = new MockScriptWriter();
    private EpisodeBuilder _builder = new(new MockNarrator(), new FfmpegRunner("ffmpeg.exe"));
    private Grounding? _grounding;
    private Func<string, Quality, Visuals>? _visualsFor; // null when there is no Azure connection
    private Hear? _hear; // null when nothing can listen to a film
    private string _imageModel = "";
    private bool _syncingClips;
    private Prefs? _reopen; // where the user was last time, until the services are connected and it can be gone back to

    public MainViewModel(bool ffmpegAvailable)
    {
        FfmpegAvailable = ffmpegAvailable;
        var prefs = Prefs.Load();
        Advanced = prefs.Advanced;
        // The video size is an advanced choice: hidden, it stays at the standard one, so nothing out of sight changes what is made.
        ExportPresetIndex = Advanced ? Math.Clamp(prefs.ExportPresetIndex, 0, ExportChoices.Length - 1) : 0;
        DraftPictures = prefs.DraftPictures;
        MonthlyBudget = Math.Max(0, prefs.MonthlyBudget);
        _reopen = prefs;
        _step = FirstStep;
        Clips.CollectionChanged += OnClipsChanged;
        Footage.CollectionChanged += (_, _) => RefreshGuide();
        // A batch must not start while a single picture is being drawn: it could ask for, and pay for, the same one.
        foreach (var one in new IAsyncRelayCommand[] { GeneratePictureCommand, AnotherTakeCommand })
            one.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning)) return;
                foreach (var command in new IRelayCommand[] { GenerateAllCommand, GeneratePictureCommand, AnotherTakeCommand, MakeDraftCommand })
                    command.NotifyCanExecuteChanged();
            };
        _ready = true;
        _ = Guard(RefreshLibraryAsync);
    }

    // ---- The live services, which arrive a moment after the window ----

    /// <summary>True once the app knows whether it is running on the live services or on stand-ins.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notice), nameof(NoticeDetail), nameof(PicturesAvailable), nameof(HostCandidatesCost), nameof(LogoCandidatesCost))]
    [NotifyCanExecuteChangedFor(nameof(CreateEpisodeCommand), nameof(GenerateAllCommand), nameof(GeneratePictureCommand), nameof(AnotherTakeCommand), nameof(MakeDraftCommand))]
    public partial bool IsConnected { get; set; }

    /// <summary>Which services are simulated and why; null when everything is live.</summary>
    public string? OfflineMessage { get; private set; }

    public bool FfmpegAvailable { get; }

    /// <param name="visualsFor">Makes the picture service for an episode folder and a quality; null when pictures are not available.</param>
    /// <param name="hear">Listens to what is said in a film; null when that is not set up.</param>
    public void Connect(IScriptWriter writer, EpisodeBuilder builder, Grounding grounding, Func<string, Quality, Visuals>? visualsFor, string imageModel, string? offlineMessage, Hear? hear = null)
    {
        (_scriptWriter, _builder, _grounding, _visualsFor, _imageModel, OfflineMessage, _hear) = (writer, builder, grounding, visualsFor, imageModel, offlineMessage, hear);
        IsConnected = true;
        Reopen();
        RedrawStill(); // opening on the intro: show it as it stands
    }

    /// <summary>
    /// Goes back to the episode, step and tab the app was closed on. Not when the user has already begun something
    /// in the moment before the services answered: what they are doing now matters more than where they were.
    /// </summary>
    private void Reopen()
    {
        var last = _reopen;
        _reopen = null;
        if (last?.LastEpisodeFolder is not { } folder || Episode is not null || TopicInput.Length > 0 || !File.Exists(Path.Combine(folder, ProjectStore.FileName)))
            return;
        try
        {
            Open(ProjectStore.Load(folder), folder, ProjectStore.LoadSnippets(folder));
            Step = Math.Clamp(last.Page, IntroStep, PublishStep);
            // The tab is set once the clips page has been laid out: its tab control picks its first tab as it first appears.
            var tab = Math.Clamp(last.InspectorTab, 0, 1);
            if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is { } later)
                later.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => InspectorTab = tab);
        }
        catch (InvalidDataException)
        {
            // Damaged since last time: it is still in the list of saved episodes, where opening it offers the repair.
        }
    }

    /// <summary>A word or two for the header when something is not as it should be, or there are messages to read back; empty otherwise.</summary>
    // Empty and not null, here and wherever a page asks a question of a value: the page is not told when a value becomes null.
    public string Notice => !IsConnected ? "Connecting…" : OfflineMessage is not null ? "Offline" : !FfmpegAvailable ? "FFmpeg missing" : Messages.Count > 0 ? "Messages" : "";

    public string NoticeDetail => string.Join("\n\n", new[]
    {
        IsConnected ? null : "Signing in to your Azure services. Saved episodes open and new ones can be written once this finishes.",
        OfflineMessage,
        FfmpegAvailable ? null : "FFmpeg is needed to preview and export. Install it, then restart the app. Everything else works without it.",
    }.OfType<string>());

    // The lists behind the drop-downs. Each entry carries its own label, so adding a choice is one line here.
    public Choice<ExportPreset>[] ExportChoices { get; } =
    [
        new("1080p · 30 fps", ExportPreset.Hd30), new("1080p · 60 fps", ExportPreset.Hd60),
        new("4K · 30 fps", ExportPreset.Uhd30), new("4K · 60 fps", ExportPreset.Uhd60),
    ];

    public Choice<CaptionPreset>[] CaptionChoices { get; } =
    [
        new("Karaoke highlight", CaptionPreset.KaraokeHighlight), new("Two-line block", CaptionPreset.TwoLineBlock),
        new("Clean subtitle", CaptionPreset.CleanSubtitle), new("Comic banner", CaptionPreset.ComicBanner),
    ];

    public ObservableCollection<ClipViewModel> Clips { get; } = [];

    /// <summary>Passages from the official repositories that the current script was grounded in.</summary>
    public ObservableCollection<GroundingSnippet> Snippets { get; } = [];

    public string SnippetsHeading => Snippets.Count == 0 ? "Sources · none for this episode" : $"Sources · {Snippets.Count} passages";

    public string? EpisodeFolder { get; private set; }

    // What the view model asks of the window and its views. Each starts as "nothing happens" and "the answer is no",
    // so the view model is whole before any view exists and never has to ask whether one has been set.

    /// <summary>Set by the view that owns the preview player; it must let go of the file before a new preview is written.</summary>
    public Action ReleasePreview { get; set; } = () => { };

    /// <summary>Set by the window: asks the user to approve something that costs money or cannot be undone. True means go ahead.</summary>
    public Func<string, string, string, Task<bool>> Confirm { get; set; } = (_, _, _) => Task.FromResult(false);

    /// <summary>
    /// Set by the window: as <see cref="Confirm"/>, for a job that costs about the dollars given, so it can be shown
    /// against the month's budget. Until set, the plain question is asked.
    /// </summary>
    public Func<string, string, string, decimal, Task<bool>> ConfirmSpend { get; set; } = (_, _, _, _) => Task.FromResult(false);

    /// <summary>Set by the window: asks for a line of text, given a title and what is there now. Null means the user backed out.</summary>
    public Func<string, string, Task<string?>> Ask { get; set; } = (_, _) => Task.FromResult<string?>(null);

    /// <summary>Set by the window: told the job's name and whether it finished, when a long job ends.</summary>
    public Action<string, bool> JobFinished { get; set; } = (_, _) => { };

    // ---- What the app remembers between runs ----

    /// <summary>The master video's size, chosen in Settings with advanced controls on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterSizeText))]
    public partial int ExportPresetIndex { get; set; }

    /// <summary>What the Publish step's master video will be, its size included, so the size is never a surprise.</summary>
    public string MasterSizeText =>
        $"The whole episode at {ExportChoices[ExportPresetIndex].Value.Height}p, {ExportChoices[ExportPresetIndex].Value.Fps} fps, wrapped in the intro and outro, with a chapter list, subtitles, a thumbnail and an upload description beside it.";

    /// <summary>Pictures are drawn at low quality: quicker and cheaper while an episode is still taking shape.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostCandidatesCost), nameof(LogoCandidatesCost))]
    public partial bool DraftPictures { get; set; }

    /// <summary>
    /// The controls most episodes never need: each clip's depth, host, picture type, takes and robot's-eye view, the jobs
    /// menu, designing the host, the outro's length, caption size and outline, teasers' moments, other video sizes and
    /// the services' model names. Hidden unless turned on.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJobs))]
    public partial bool Advanced { get; set; }

    partial void OnAdvancedChanged(bool value)
    {
        foreach (var card in TeaserCards)
            card.Advanced = value;
        if (value) return;
        // As when the app starts with them hidden: nothing out of sight may change what is made.
        ExportPresetIndex = 0;
        foreach (var card in TeaserCards)
            card.Reset();
    }

    // Everything remembered is saved as it changes. Adding a choice to remember is one name here and one in SavePrefs.
    private static readonly HashSet<string> Remembered =
    [
        nameof(ExportPresetIndex), nameof(DraftPictures), nameof(Step), nameof(InspectorTab), nameof(MonthlyBudget), nameof(Advanced),
    ];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (Remembered.Contains(e.PropertyName ?? "")) SavePrefs();
        RefreshGuide(e);
    }

    private void SavePrefs()
    {
        if (_ready) new Prefs(ExportPresetIndex, DraftPictures, EpisodeFolder, Step, InspectorTab, MonthlyBudget, Advanced).Save();
    }

    // ---- Episode ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateEpisodeCommand))]
    [NotifyPropertyChangedFor(nameof(TopicCount))]
    public partial string TopicInput { get; set; } = "";

    /// <summary>What went wrong on the way, in the owner's words; optional.</summary>
    [ObservableProperty]
    public partial string WentWrongInput { get; set; } = "";

    /// <summary>The next episode's place in the series. A number box gives a double; it is rounded when the episode is made.</summary>
    [ObservableProperty]
    public partial double EpisodeNumber { get; set; } = 1;

    public Choice<BeatStructure>[] StructureChoices { get; } =
    [
        new("Story", BeatStructure.Story), new("Tutorial", BeatStructure.Tutorial), new("Hook first", BeatStructure.HookFirst),
    ];

    /// <summary>The shape of the next episode: the series' own, set on the Intro step.</summary>
    private BeatStructure NextStructure => _kit.Structure;

    /// <summary>The beats of the chosen structure in order, marking those where footage belongs.</summary>
    public string StructureBeats => string.Join(" → ", BeatPlan.Beats(NextStructure).Select(b => b.ForFootage ? $"{b.Name} 🎥" : b.Name));

    /// <summary>How much of the room for a topic is used, said beside the box so a long paste is not cut short unnoticed.</summary>
    public string TopicCount => TopicInput.Length >= IScriptWriter.MaxTopicLength
        ? $"{TopicInput.Length:N0} of {IScriptWriter.MaxTopicLength:N0} characters · full: anything pasted beyond this is left out"
        : $"{TopicInput.Length:N0} of {IScriptWriter.MaxTopicLength:N0} characters";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EpisodeTitle), nameof(HasEpisode), nameof(Runtime), nameof(Captions),
        nameof(CaptionPresetIndex), nameof(CaptionFontSize), nameof(CaptionStroke), nameof(CaptionsTuned), nameof(CaptionAccent), nameof(HasMusic), nameof(MusicLabel), nameof(EpisodeOffSeriesLook))]
    [NotifyCanExecuteChangedFor(nameof(RenderCommand), nameof(SuggestTeasersCommand), nameof(RenderTeasersCommand), nameof(UseSeriesLookCommand), nameof(BuildPreviewCommand), nameof(GenerateAllCommand), nameof(NarrateAllCommand),
        nameof(CleanMediaCommand), nameof(RenameEpisodeCommand), nameof(MakeDraftCommand), nameof(RemoveMusicCommand),
        nameof(WatchClipCommand), nameof(WatchTeaserCommand))]
    public partial Episode? Episode { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>The outcome of the last action, shown on every step.</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public bool HasEpisode => Episode is not null;

    public string EpisodeTitle => Episode?.Title ?? "No episode yet";

    /// <summary>Length of the finished video: measured for clips whose voice is recorded, estimated for the rest.</summary>
    public TimeSpan Runtime => TimeSpan.FromTicks(Clips.Sum(c => c.Duration.Ticks));

    /// <summary>What this episode's pictures have cost so far, in dollars.</summary>
    public decimal Spent { get; private set; }

    private string _usage = "";

    /// <summary>
    /// The line under the title: clip count, running time and what has been spent. The window passes the running
    /// time and cost it is showing, so the two can roll to a new value instead of jumping.
    /// </summary>
    public string MetaText(double runtimeSeconds, double spent)
    {
        if (Episode is null) return "";
        var total = TimeSpan.FromSeconds(runtimeSeconds);
        return string.Join(" · ", new[]
        {
            Episode.Clips.Count == 1 ? "1 clip" : $"{Episode.Clips.Count} clips",
            Durations.RuntimeWarning(total) is { } off ? $"runtime {total:m\\:ss}, {off}" : $"runtime {total:m\\:ss}",
            spent >= 0.005 ? $"spent about {SpendLog.Money((decimal)spent)}" : "",
            _usage,
        }.Where(part => part.Length > 0));
    }

    /// <summary>Reads the episode's running cost again and tells the header that its figures have changed.</summary>
    private void RefreshMeta()
    {
        if (EpisodeFolder is { } folder)
        {
            Spent = SpendLog.Total(folder);
            var (tokens, characters) = (SpendLog.Units(folder, SpendLog.ScriptTokens), SpendLog.Units(folder, SpendLog.VoiceCharacters));
            _usage = string.Join(" · ", new[] { tokens > 0 ? $"{tokens:N0} script tokens" : "", characters > 0 ? $"{characters:N0} voice characters" : "" }.Where(part => part.Length > 0));
        }
        OnPropertyChanged(nameof(Runtime));
        RefreshGuide(); // a voice or a picture may just have been made
    }

    private readonly List<(string What, int Units, decimal Dollars)> _usedBeforeFolder = []; // a script is written before its episode has a folder

    /// <summary>
    /// The script and voice services report what each request used, with its price when the user has given their
    /// rates and zero when not. Safe to call from any thread.
    /// </summary>
    public void LogUsage(string what, int units, decimal dollars)
    {
        lock (_usedBeforeFolder)
        {
            if (EpisodeFolder is { } folder && !IsCreating)
                SpendLog.Add(folder, what, dollars, units);
            else
                _usedBeforeFolder.Add((what, units, dollars));
        }
    }

    private ClipViewModel NewCard(Clip clip) => new(clip, Edit, PickTier, Watch);
}
