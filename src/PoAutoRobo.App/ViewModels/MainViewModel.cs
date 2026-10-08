using System.Collections.ObjectModel;
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
    private Func<string, string, Visuals>? _visualsFor; // null when there is no Azure connection
    private string _imageModel = "";
    private bool _syncingClips;

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
        SubjectIndex = Math.Clamp(prefs.SubjectIndex, 0, SubjectChoices.Length - 1);
        Clips.CollectionChanged += OnClipsChanged;
        // A batch must not start while a single picture is being drawn: it could ask for, and pay for, the same one.
        GeneratePictureCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning)) GenerateAllCommand.NotifyCanExecuteChanged();
        };
        _ready = true;
        _ = Guard(RefreshLibraryAsync);
    }

    // ---- The live services, which arrive a moment after the window ----

    /// <summary>True once the app knows whether it is running on the live services or on stand-ins.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notice), nameof(NoticeDetail), nameof(PicturesAvailable), nameof(HostCandidatesCost))]
    [NotifyCanExecuteChangedFor(nameof(CreateEpisodeCommand), nameof(GenerateAllCommand), nameof(GeneratePictureCommand))]
    public partial bool IsConnected { get; set; }

    /// <summary>Which services are simulated and why; null when everything is live.</summary>
    public string? OfflineMessage { get; private set; }

    public bool FfmpegAvailable { get; }

    /// <param name="visualsFor">Makes the picture service for an episode folder and a quality; null when pictures are not available.</param>
    public void Connect(IScriptWriter writer, EpisodeBuilder builder, Grounding grounding, Func<string, string, Visuals>? visualsFor, string imageModel, string? offlineMessage)
    {
        (_scriptWriter, _builder, _grounding, _visualsFor, _imageModel, OfflineMessage) = (writer, builder, grounding, visualsFor, imageModel, offlineMessage);
        IsConnected = true;
    }

    /// <summary>A word or two for the header when something is not as it should be; empty when all is well.</summary>
    // Empty and not null, here and wherever a page asks a question of a value: the page is not told when a value becomes null.
    public string Notice => !IsConnected ? "Connecting…" : OfflineMessage is not null ? "Offline" : !FfmpegAvailable ? "FFmpeg missing" : "";

    public string NoticeDetail => string.Join("\n\n", new[]
    {
        IsConnected ? null : "Signing in to your Azure services. Saved episodes open and new ones can be written once this finishes.",
        OfflineMessage,
        FfmpegAvailable ? null : "FFmpeg is needed to preview and export. Install it, then restart the app. Everything else works without it.",
    }.OfType<string>());

    // The lists behind the drop-downs. Each entry carries its own label, so adding a choice is one line here.
    public Choice<EpisodeLength>[] LengthChoices { get; } =
    [
        new("Full episode · 15 to 20 clips, about 10 minutes", EpisodeLength.Full),
        new("Short · 5 clips, about 3 minutes", EpisodeLength.Short),
        new("Two clips · about 1 minute", EpisodeLength.TwoClips),
        new("Quick test · 1 clip, about 30 seconds", EpisodeLength.QuickTest),
    ];

    public Choice<Subject>[] SubjectChoices { get; } =
    [
        new("Unitree R1 · grounded in the official repositories", Subject.UnitreeR1),
        new("Any topic · written from what you type", Subject.General),
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

    /// <summary>Set by the view that owns the audio player.</summary>
    public Action<string>? PlayAudio { get; set; }

    /// <summary>Set by the view that owns the preview player; it must let go of the file before a new preview is written.</summary>
    public Action? ReleasePreview { get; set; }

    /// <summary>Set by the window: asks the user to approve something that costs money or cannot be undone. True means go ahead.</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Set by the window: asks for a line of text, given a title and what is there now. Null means the user backed out.</summary>
    public Func<string, string, Task<string?>>? Ask { get; set; }

    /// <summary>Set by the window: told the job's name and whether it finished, when a long job ends.</summary>
    public Action<string, bool>? JobFinished { get; set; }

    /// <summary>Set by the window: one clip of a batch is done, and whereabouts in the batch it is (0 first, 1 last).</summary>
    public Action<double>? ClipDone { get; set; }

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
        SavePrefs();
        if (_ready) RefreshTopicsCommand.Execute(null);
    }

    partial void OnLengthIndexChanged(int value) => SavePrefs();

    partial void OnExportPresetIndexChanged(int value) => SavePrefs();

    partial void OnSoundsOnChanged(bool value) => SavePrefs();

    partial void OnSoundVolumeChanged(double value) => SavePrefs();

    partial void OnDraftPicturesChanged(bool value) => SavePrefs();

    private void SavePrefs()
    {
        if (_ready) new Prefs(LengthIndex, ExportPresetIndex, SoundsOn, SubjectIndex, SoundVolume, DraftPictures).Save();
    }

    // ---- Steps ----

    private static readonly string[] StepHints =
    [
        "Pick the one topic for this episode: type your own, adopt a story, or open an episode you saved earlier.",
        "Shape the clips: drag them into order, edit and audition each one's words, then give each its picture.",
        "Finish: choose the caption style, watch a preview, then render the master video.",
    ];

    /// <summary>Which page is showing: 0 Topic, 1 Clips, 2 Export.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTopic), nameof(ShowDeck), nameof(StepHint))]
    public partial int Step { get; set; }

    // While a script is being written the deck page shows in place of the topic page, with placeholder cards.
    public bool ShowTopic => Step == 0 && !IsCreating;

    public bool ShowDeck => Step == 1 || IsCreating;

    public string StepHint => StepHints[Math.Clamp(Step, 0, StepHints.Length - 1)];

    /// <summary>Which half of the inspector is showing: 0 the clip's words, 1 its picture.</summary>
    [ObservableProperty]
    public partial int InspectorTab { get; set; }

    partial void OnStepChanged(int value)
    {
        if (value > 0 && !HasEpisode) Step = 0; // the later pages have nothing to show until there is an episode
        if (value == 0) _ = Guard(RefreshLibraryAsync);
        ErrorMessage = null; // an error belongs to the page it happened on
    }

    /// <summary>For the keyboard shortcuts: goes to a step when it is open.</summary>
    public void GoTo(int step)
    {
        if (step == 0 || HasEpisode) Step = step;
    }

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
                : subject == Subject.General ? await _trendFeed.GetGeneralAsync(ct)
                : await _trendFeed.GetAsync(ct);
            ct.ThrowIfCancellationRequested();
            var line = typed.Split('\n')[0].Trim();
            RadarHeading = typed.Length > 0 ? $"Topic radar · reading on “{(line.Length > 60 ? line[..60] + "…" : line)}”{(cards.Count == 0 ? " · none found" : "")}"
                : subject == Subject.General ? "Topic radar · popular today"
                : "Topic radar · Unitree R1";
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
    public partial string TopicInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EpisodeTitle), nameof(HasEpisode), nameof(Runtime), nameof(Captions),
        nameof(CaptionPresetIndex), nameof(CaptionFontSize), nameof(CaptionStroke), nameof(CaptionAccent))]
    [NotifyCanExecuteChangedFor(nameof(RenderCommand), nameof(RenderShortsCommand), nameof(BuildPreviewCommand), nameof(GenerateAllCommand), nameof(NarrateAllCommand),
        nameof(CleanMediaCommand), nameof(ShowEpisodeFolderCommand), nameof(RenameEpisodeCommand))]
    public partial Episode? Episode { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>The outcome of the last action, shown on every step.</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

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
            spent >= 0.005 ? "spent " + SpendLog.InWords((decimal)spent) : "",
            _usage,
        }.Where(part => part.Length > 0));
    }

    /// <summary>Reads the episode's running cost again and tells the header that its figures have changed.</summary>
    private void RefreshMeta()
    {
        if (EpisodeFolder is { } folder)
        {
            Spent = SpendLog.Total(folder);
            var (tokens, characters) = (SpendLog.Units(folder, ScriptTokens), SpendLog.Units(folder, VoiceCharacters));
            _usage = string.Join(" · ", new[] { tokens > 0 ? $"{tokens:N0} script tokens" : "", characters > 0 ? $"{characters:N0} voice characters" : "" }.Where(part => part.Length > 0));
        }
        OnPropertyChanged(nameof(Runtime));
    }

    private const string ScriptTokens = "script tokens";
    private const string VoiceCharacters = "voice characters";
    private readonly List<(string What, int Units)> _usedBeforeFolder = []; // a script is written before its episode has a folder

    /// <summary>
    /// The script and voice services report what each request used. They have no price the app knows, so the
    /// amounts are listed without one. Safe to call from any thread.
    /// </summary>
    public void LogUsage(string what, int units)
    {
        lock (_usedBeforeFolder)
        {
            if (EpisodeFolder is { } folder && !IsCreating)
                SpendLog.Add(folder, what, 0, units);
            else
                _usedBeforeFolder.Add((what, units));
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
            var episode = VisualMix.Assign(await _scriptWriter.WriteEpisodeAsync(topic, subject, grounding, length, ct, written), MixPercentages.Default);
            var folder = ProjectStore.NewFolder(AppPaths.Episodes, episode.Title); // never on top of an earlier episode
            ProjectStore.Save(episode, folder);
            ProjectStore.SaveSnippets(grounding, folder);
            lock (_usedBeforeFolder)
            {
                foreach (var (what, units) in _usedBeforeFolder)
                    SpendLog.Add(folder, what, 0, units);
                _usedBeforeFolder.Clear();
            }
            Open(episode, folder, grounding);
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
        ReleasePreview?.Invoke();
        Preview = null;
        LastExport = null;
        ForgetHistory();
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
        _ = Guard(ShowFootageAsync);
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
        if (Ask is not null && Episode is { } episode && await Ask("Rename episode", episode.Title) is { } title)
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
        if (Confirm is null || !await Confirm("Remove unused media?", message, "Remove") || IsWorking)
            return;
        ForgetHistory(); // an undone edit could point at a file that is about to go
        foreach (var file in unused)
            File.Delete(file);
        StatusMessage = $"Removed {unused.Count} unused files ({megabytes:0.0} MB).";
    });
}
