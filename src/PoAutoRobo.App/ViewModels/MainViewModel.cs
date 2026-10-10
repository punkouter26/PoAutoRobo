using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PoAutoRobo.App.ViewModels;

/// <summary>
/// The app's one view model. This file holds what it is connected to, the steps and the topic page; the other
/// parts are beside it: Library, Edits, Pictures, Export and Activity.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly TrendFeed _trendFeed;
    private readonly bool _ready; // false while the constructor restores saved choices, so restoring them saves and fetches nothing

    // Simulated until Connect is called: the window opens before the Azure sign-in has answered.
    private IScriptWriter _scriptWriter = new MockScriptWriter();
    private EpisodeBuilder _builder = new(new MockNarrator(), new FfmpegRunner("ffmpeg.exe"));
    private Grounding? _grounding;
    private Func<string, Quality, Visuals>? _visualsFor; // null when there is no Azure connection
    private string _imageModel = "";
    private bool _syncingClips;
    private Prefs? _reopen; // where the user was last time, until the services are connected and it can be gone back to

    public MainViewModel(TrendFeed trendFeed, bool ffmpegAvailable)
    {
        _trendFeed = trendFeed;
        FfmpegAvailable = ffmpegAvailable;
        var prefs = Prefs.Load();
        LengthIndex = Math.Clamp(prefs.LengthIndex, 0, LengthChoices.Length - 1);
        ExportPresetIndex = Math.Clamp(prefs.ExportPresetIndex, 0, ExportChoices.Length - 1);
        SoundsOn = prefs.SoundsOn;
        SoundVolume = Math.Clamp(prefs.SoundVolume, 0, 1);
        DraftPictures = prefs.DraftPictures;
        MonthlyBudget = Math.Max(0, prefs.MonthlyBudget);
        SubjectIndex = Math.Clamp(prefs.SubjectIndex, 0, SubjectChoices.Length - 1);
        _reopen = prefs;
        Clips.CollectionChanged += OnClipsChanged;
        // A batch must not start while a single picture is being drawn: it could ask for, and pay for, the same one.
        foreach (var one in new IAsyncRelayCommand[] { GeneratePictureCommand, AnotherTakeCommand })
            one.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning)) return;
                foreach (var command in new IRelayCommand[] { GenerateAllCommand, GeneratePictureCommand, AnotherTakeCommand, MakeAllCommand })
                    command.NotifyCanExecuteChanged();
            };
        _ready = true;
        _ = Guard(RefreshLibraryAsync);
    }

    // ---- The live services, which arrive a moment after the window ----

    /// <summary>True once the app knows whether it is running on the live services or on stand-ins.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notice), nameof(NoticeDetail), nameof(PicturesAvailable), nameof(HostCandidatesCost))]
    [NotifyCanExecuteChangedFor(nameof(CreateEpisodeCommand), nameof(GenerateAllCommand), nameof(GeneratePictureCommand), nameof(AnotherTakeCommand), nameof(MakeAllCommand))]
    public partial bool IsConnected { get; set; }

    /// <summary>Which services are simulated and why; null when everything is live.</summary>
    public string? OfflineMessage { get; private set; }

    public bool FfmpegAvailable { get; }

    /// <param name="visualsFor">Makes the picture service for an episode folder and a quality; null when pictures are not available.</param>
    public void Connect(IScriptWriter writer, EpisodeBuilder builder, Grounding grounding, Func<string, Quality, Visuals>? visualsFor, string imageModel, string? offlineMessage)
    {
        (_scriptWriter, _builder, _grounding, _visualsFor, _imageModel, OfflineMessage) = (writer, builder, grounding, visualsFor, imageModel, offlineMessage);
        IsConnected = true;
        Reopen();
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
            Step = last.Step;
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
    public Choice<EpisodeLength>[] LengthChoices { get; } =
    [
        new("Full (15–20)", EpisodeLength.Full),
        new("Short (5)", EpisodeLength.Short),
        new("Two clips", EpisodeLength.TwoClips),
        new("Quick test (1)", EpisodeLength.QuickTest),
        // New lengths go on the end: the one last used is remembered by its place in this list.
        new("Sampler (9)", new EpisodeLength(9, 9)),
    ];

    public Choice<Subject>[] SubjectChoices { get; } =
    [
        new("Unitree R1", Subject.UnitreeR1),
        new("Any topic", Subject.General),
        new("Video essay", Subject.Essay),
    ];

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

    /// <summary>Set by the view that owns the audio player.</summary>
    public Action<string> PlayAudio { get; set; } = _ => { };

    /// <summary>Set by the view that owns the preview player; it must let go of the file before a new preview is written.</summary>
    public Action ReleasePreview { get; set; } = () => { };

    /// <summary>Set by the window: asks the user to approve something that costs money or cannot be undone. True means go ahead.</summary>
    public Func<string, string, string, Task<bool>> Confirm { get; set; } = (_, _, _) => Task.FromResult(false);

    /// <summary>Set by the window: asks for a line of text, given a title and what is there now. Null means the user backed out.</summary>
    public Func<string, string, Task<string?>> Ask { get; set; } = (_, _) => Task.FromResult<string?>(null);

    /// <summary>Set by the window: told the job's name and whether it finished, when a long job ends.</summary>
    public Action<string, bool> JobFinished { get; set; } = (_, _) => { };

    /// <summary>Set by the window: one clip of a batch is done, and whereabouts in the batch it is (0 first, 1 last).</summary>
    public Action<double> ClipDone { get; set; } = _ => { };

    /// <summary>Set by the window: plays one of the app's own sounds.</summary>
    public Action<Cue> PlayCue { get; set; } = _ => { };

    // ---- What the app remembers between runs ----

    /// <summary>Length of the next episode, as an index into <see cref="LengthChoices"/>.</summary>
    [ObservableProperty]
    public partial int LengthIndex { get; set; }

    [ObservableProperty]
    public partial int ExportPresetIndex { get; set; }

    [ObservableProperty]
    public partial bool SoundsOn { get; set; }

    /// <summary>How loud the app's sounds are, 0 to 1.</summary>
    [ObservableProperty]
    public partial double SoundVolume { get; set; }

    /// <summary>Pictures are drawn at low quality: quicker and cheaper while an episode is still taking shape.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostCandidatesCost))]
    public partial bool DraftPictures { get; set; }

    /// <summary>What the next episode is about, as an index into <see cref="SubjectChoices"/>.</summary>
    [ObservableProperty]
    public partial int SubjectIndex { get; set; }

    private Subject NextSubject => SubjectChoices[Math.Clamp(SubjectIndex, 0, SubjectChoices.Length - 1)].Value;

    /// <summary>What the radar is showing: stories on the subject, or stories about the topic typed.</summary>
    [ObservableProperty]
    public partial string RadarHeading { get; set; } = "Topic radar";

    // The radar follows the subject: R1 stories for R1 episodes, today's popular stories otherwise.
    partial void OnSubjectIndexChanged(int value)
    {
        if (_ready) RefreshTopicsCommand.Execute(null);
    }

    // Everything remembered is saved as it changes. Adding a choice to remember is one name here and one in SavePrefs.
    private static readonly HashSet<string> Remembered =
    [
        nameof(LengthIndex), nameof(ExportPresetIndex), nameof(SoundsOn), nameof(SubjectIndex), nameof(SoundVolume), nameof(DraftPictures),
        nameof(Step), nameof(InspectorTab), nameof(MonthlyBudget),
    ];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (Remembered.Contains(e.PropertyName ?? "")) SavePrefs();
    }

    private void SavePrefs()
    {
        if (_ready) new Prefs(LengthIndex, ExportPresetIndex, SoundsOn, SubjectIndex, SoundVolume, DraftPictures, EpisodeFolder, Step, InspectorTab, MonthlyBudget).Save();
    }

    // ---- Steps ----

    private static readonly string[] StepHints =
    [
        "Pick the one topic for this episode: type your own, adopt a story, or open an episode you saved earlier.",
        "Shape the clips: drag them into order, edit and audition each one's words, then give each its picture.",
        "Finish: choose the caption style, watch a preview, then render the master video.",
    ];

    private int _step;

    /// <summary>
    /// Which page is showing: 0 Topic, 1 Clips, 2 Export. The later pages have nothing to show until there is an
    /// episode, so asking for one then gives the first; the asker is told, and shows the page really on.
    /// </summary>
    public int Step
    {
        get => _step;
        set
        {
            var wanted = value is 1 or 2 && HasEpisode ? value : 0;
            if (wanted == _step)
            {
                if (wanted != value) OnPropertyChanged(nameof(Step));
                return;
            }
            _step = wanted;
            OnPropertyChanged(nameof(Step));
            OnPropertyChanged(nameof(ShowTopic));
            OnPropertyChanged(nameof(ShowDeck));
            OnPropertyChanged(nameof(StepHint));
            if (wanted == 0) _ = Guard(RefreshLibraryAsync);
            ErrorMessage = null; // an error belongs to the page it happened on
        }
    }

    // While a script is being written the deck page shows in place of the topic page, with placeholder cards.
    public bool ShowTopic => Step == 0 && !IsCreating;

    public bool ShowDeck => Step == 1 || IsCreating;

    public string StepHint => StepHints[Math.Clamp(Step, 0, StepHints.Length - 1)];

    /// <summary>Which half of the inspector is showing: 0 the clip's words, 1 its picture.</summary>
    [ObservableProperty]
    public partial int InspectorTab { get; set; }

    /// <summary>For the keyboard shortcuts.</summary>
    public void GoTo(int step) => Step = step;

    // ---- Topic radar ----

    public ObservableCollection<TopicCard> Topics { get; } = [];

    [RelayCommand]
    private async Task RefreshTopicsAsync(CancellationToken ct)
    {
        try
        {
            // Never throws for a dead source. R1 falls back to sample topics; the other lists are simply empty.
            // A topic already typed comes first: the radar shows stories about that, whatever the subject is set to.
            var typed = TopicInput.Trim();
            var subject = NextSubject;
            var cards = typed.Length > 0 ? await _trendFeed.SearchAsync(typed, ct)
                : subject == Subject.UnitreeR1 ? await _trendFeed.GetAsync(ct)
                : await _trendFeed.GetGeneralAsync(ct);
            ct.ThrowIfCancellationRequested();
            var line = typed.Split('\n')[0].Trim();
            RadarHeading = typed.Length > 0 ? $"Topic radar · reading on “{(line.Length > 60 ? line[..60] + "…" : line)}”{(cards.Count == 0 ? " · none found" : "")}"
                : subject == Subject.UnitreeR1 ? "Topic radar · Unitree R1"
                : "Topic radar · popular today";
            Topics.Clear();
            foreach (var card in cards)
                Topics.Add(card);
        }
        catch (OperationCanceledException)
        {
            // Asking again (a new subject, the refresh button) cancels the request before it. Left uncaught, that
            // cancellation leaves the command as an unhandled error and closes the app.
        }
    }

    private int _radarRequest;

    // The radar follows the topic box, once the typing pauses. Not during a job: adopting a story fills the box
    // and starts the script, and the stories on show should stay put while it is written.
    partial void OnTopicInputChanged(string value)
    {
        if (!_adopting) RefreshTopicsSoon();
    }

    private async void RefreshTopicsSoon()
    {
        var request = ++_radarRequest;
        await Task.Delay(700);
        if (request == _radarRequest && _ready && !IsWorking) RefreshTopicsCommand.Execute(null);
    }

    /// <summary>Takes a card as the episode's one topic and starts writing.</summary>
    [RelayCommand]
    private async Task AdoptTopicAsync(TopicCard card)
    {
        // Running a command directly skips its own "can this run now" check, so it is asked here: without it a
        // second click, or a click during a render, would start (and pay for) a second script.
        if (IsWorking) return;
        _adopting = true; // the box is about to be filled with this story; that is not the user asking for others
        TopicInput = card.Summary.Length > 0 ? $"{card.Title}\n\n{card.Summary}" : card.Title;
        _radarRequest++;
        _adopting = false;
        if (CreateEpisodeCommand.CanExecute(null))
            await CreateEpisodeCommand.ExecuteAsync(null);
    }

    private bool _adopting;

    // ---- Episode ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateEpisodeCommand))]
    [NotifyPropertyChangedFor(nameof(TopicCount))]
    public partial string TopicInput { get; set; } = "";

    /// <summary>How much of the room for a topic is used, said beside the box so a long paste is not cut short unnoticed.</summary>
    public string TopicCount => TopicInput.Length >= IScriptWriter.MaxTopicLength
        ? $"{TopicInput.Length:N0} of {IScriptWriter.MaxTopicLength:N0} characters · full: anything pasted beyond this is left out"
        : $"{TopicInput.Length:N0} of {IScriptWriter.MaxTopicLength:N0} characters";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EpisodeTitle), nameof(HasEpisode), nameof(Runtime), nameof(Captions),
        nameof(CaptionPresetIndex), nameof(CaptionFontSize), nameof(CaptionStroke), nameof(CaptionAccent), nameof(HasMusic), nameof(MusicLabel))]
    [NotifyCanExecuteChangedFor(nameof(RenderCommand), nameof(RenderShortsCommand), nameof(BuildPreviewCommand), nameof(GenerateAllCommand), nameof(NarrateAllCommand),
        nameof(CleanMediaCommand), nameof(ShowEpisodeFolderCommand), nameof(RenameEpisodeCommand), nameof(MakeAllCommand), nameof(ReviewScriptCommand), nameof(RemoveMusicCommand))]
    public partial Episode? Episode { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>The outcome of the last action, shown on every step.</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    // ---- What has been said, kept to read back ----

    private const int MessagesKept = 20;

    /// <summary>The last things the app said, newest first, each with the time. The message bar shows only the latest.</summary>
    public ObservableCollection<string> Messages { get; } = [];

    partial void OnErrorMessageChanged(string? value) => Remember(value);

    partial void OnStatusMessageChanged(string? value) => Remember(value);

    private void Remember(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        Messages.Insert(0, $"{DateTime.Now:HH:mm} · {message}");
        while (Messages.Count > MessagesKept)
            Messages.RemoveAt(Messages.Count - 1);
        OnPropertyChanged(nameof(Notice));
    }

    /// <summary>The user closed the message bar. Clearing both means the same message, said again, shows again.</summary>
    public void DismissMessages() => (ErrorMessage, StatusMessage) = (null, null);

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
            Durations.IsShort(total) ? $"runtime {total:m\\:ss}, shorter than the 3 minute target" : $"runtime {total:m\\:ss}",
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

    /// <summary>True while a script is being written; the deck shows placeholder cards meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTopic), nameof(ShowDeck))]
    public partial bool IsCreating { get; set; }

    /// <summary>One placeholder per clip the script being written may have: a clip's title once it is written, empty until then.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Skeletons { get; set; } = [];

    // Not while a long job runs: it works on the current episode and must not have it swapped from under it.
    private bool CanCreateEpisode => !string.IsNullOrWhiteSpace(TopicInput) && !IsWorking && IsConnected;

    [RelayCommand(CanExecute = nameof(CanCreateEpisode))]
    private Task CreateEpisodeAsync() => RunActivityAsync("Writing the script", async ct =>
    {
        var topic = TopicInput.Trim();
        var length = LengthChoices[LengthIndex].Value;
        Skeletons = [.. Enumerable.Repeat("", length.MaxClips)];
        IsCreating = true;
        try
        {
            // Only R1 episodes are grounded: the repositories have nothing to say about other subjects.
            var subject = NextSubject;
            IReadOnlyList<GroundingSnippet> grounding = [];
            if (subject == Subject.UnitreeR1 && _grounding is not null)
            {
                ReportActivity("Reading the official repositories", 0.02);
                grounding = await _grounding.FindAsync(topic, ct);
            }
            ReportActivity("Writing the script", 0.1);
            var of = length.MinClips == length.MaxClips ? $"{length.MaxClips}" : $"up to {length.MaxClips}";
            // Each finished clip's title lands on its placeholder card while the rest are still being written.
            var written = new Progress<IReadOnlyList<string>>(titles =>
            {
                if (!IsCreating) return;
                ReportActivity($"Written {titles.Count} of {of} clips", 0.1 + 0.9 * Math.Min(1.0, (double)titles.Count / length.MaxClips));
                Skeletons = [.. titles.Take(length.MaxClips), .. Enumerable.Repeat("", Math.Max(0, length.MaxClips - titles.Count))];
            });
            // The script chooses each clip's picture type; whatever cannot be made on this computer becomes a still.
            Func<VisualKind, bool> canMake = CurrentVisuals is { } visuals ? visuals.CanMake : _ => false;
            var episode = VisualMix.Settle(await _scriptWriter.WriteEpisodeAsync(topic, subject, grounding, length, ct, written), canMake);
            var folder = ProjectStore.NewFolder(AppPaths.Episodes, episode.Title); // never on top of an earlier episode
            ProjectStore.Save(episode, folder);
            ProjectStore.SaveSnippets(grounding, folder);
            lock (_usedBeforeFolder)
            {
                foreach (var (what, units, dollars) in _usedBeforeFolder)
                    SpendLog.Add(folder, what, dollars, units);
                _usedBeforeFolder.Clear();
            }
            Open(episode, folder, grounding);
            _ = ReviewAsync(quiet: true); // read by an editor while the user looks the clips over; nothing waits for it
        }
        finally
        {
            IsCreating = false;
        }
    });

    /// <summary>Makes an episode the one being worked on, and lands on its clips.</summary>
    private void Open(Episode episode, string folder, IReadOnlyList<GroundingSnippet> snippets)
    {
        Flush(); // a caption change to the episode being left may still be waiting to be saved
        _syncingClips = true;
        ReleasePreview();
        Preview = null;
        LastExport = null;
        ForgetHistory();
        _notes.Clear();
        EpisodeFolder = folder;
        Snippets.Clear();
        foreach (var snippet in snippets)
            Snippets.Add(snippet);
        OnPropertyChanged(nameof(SnippetsHeading));
        Episode = episode;
        Clips.Clear();
        foreach (var clip in episode.Clips)
            Clips.Add(NewCard(clip));
        _syncingClips = false;
        SyncCards();
        SelectedClip = Clips.FirstOrDefault();
        Step = 1;
        SavePrefs(); // this is now the episode to come back to
        _ = Guard(ShowFootageAsync);
    }

    // ---- The editor's notes ----

    private readonly Dictionary<Guid, string> _notes = []; // ponytail: not saved with the episode; ask again after reopening

    /// <summary>Has the whole script read as an editor would, and marks the clips worth changing.</summary>
    [RelayCommand(CanExecute = nameof(HasEpisode))]
    private Task ReviewScriptAsync() => ReviewAsync(quiet: false);

    /// <param name="quiet">Asked for by the app and not the user: nothing is said when it finds nothing or cannot be done.</param>
    private async Task ReviewAsync(bool quiet)
    {
        if (Episode is not { } episode || EpisodeFolder is not { } folder) return;
        try
        {
            var notes = await _scriptWriter.ReviewAsync(episode, CancellationToken.None);
            if (EpisodeFolder != folder) return; // another episode was opened meanwhile
            _notes.Clear();
            foreach (var note in notes)
                _notes[episode.Clips[note.Clip - 1].Id] = note.Note;
            SyncCards();
            if (notes.Count > 0)
                StatusMessage = $"An editor's read of the script has a note on {(notes.Count == 1 ? "1 clip" : $"{notes.Count} clips")}. Point at a card's warning mark to read it.";
            else if (!quiet)
                StatusMessage = "An editor's read of the script found nothing to change.";
        }
        catch (Exception e) when (quiet && e is not OperationCanceledException)
        {
            // A help nobody asked for: when it cannot be had, the episode is no worse off.
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ErrorMessage = Plain(e);
        }
    }

    /// <summary>Footage added before cards showed a frame of it has none saved; take one now so those cards are not blank.</summary>
    private async Task ShowFootageAsync()
    {
        if (!FfmpegAvailable) return;
        foreach (var card in Clips.Where(c => c.HasUserVideo && c.ThumbnailPath.Length == 0).ToList())
        {
            if (card.Clip.Visual.UserVideoPath is not { } video || !File.Exists(video)) continue;
            await _builder.PosterAsync(video, CancellationToken.None);
            card.PictureChanged();
        }
    }

    private ClipViewModel NewCard(Clip clip) => new(clip, Edit, PickTier);

    [RelayCommand(CanExecute = nameof(HasEpisode))]
    private async Task RenameEpisodeAsync()
    {
        if (Episode is { } episode && await Ask("Rename episode", episode.Title) is { } title)
            Edit(e => EpisodeEditor.Rename(e, title));
    }

    // ---- Housekeeping ----

    [RelayCommand(CanExecute = nameof(HasEpisode))]
    private void ShowEpisodeFolder() => ShowInExplorer(EpisodeFolder!);

    /// <summary>Opens File Explorer on a folder, or on the folder holding a file with that file picked out.</summary>
    public static void ShowInExplorer(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\""))?.Dispose();

    /// <summary>Removes recordings of earlier wording and replaced pictures from the episode folder, after asking.</summary>
    [RelayCommand(CanExecute = nameof(CanStartJob))]
    private Task CleanMediaAsync() => Guard(async () =>
    {
        var unused = _builder.UnusedMedia(Episode!, EpisodeFolder!);
        if (unused.Count == 0)
        {
            StatusMessage = "Nothing to remove: every saved file is in use.";
            return;
        }
        var megabytes = unused.Sum(file => new FileInfo(file).Length) / 1048576.0;
        var message = $"{unused.Count} files ({megabytes:0.0} MB) are no longer used by any clip: recordings of earlier wording and replaced pictures. " +
            "They are deleted for good, and a removed picture would be charged again if you went back to it. Undo history is cleared.";
        if (!await Confirm("Remove unused media?", message, "Remove") || IsWorking)
            return;
        ForgetHistory(); // an undone edit could point at a file that is about to go
        foreach (var file in unused)
            File.Delete(file);
        StatusMessage = $"Removed {unused.Count} unused files ({megabytes:0.0} MB).";
    });
}
