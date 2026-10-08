using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;
using Windows.UI;

namespace PoAutoRobo.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly ExportPreset[] ExportPresets = [ExportPreset.Hd30, ExportPreset.Hd60, ExportPreset.Uhd30, ExportPreset.Uhd60];

    private readonly IScriptWriter _scriptWriter;
    private readonly EpisodeBuilder _builder;
    private readonly Grounding _grounding;
    private readonly TrendFeed _trendFeed;
    private readonly Func<string, Visuals>? _visualsFor; // null when there is no Azure connection
    private readonly string _imageModel;
    private bool _syncingClips;

    public MainViewModel(IScriptWriter scriptWriter, EpisodeBuilder builder, Grounding grounding, TrendFeed trendFeed, Func<string, Visuals>? visualsFor, string imageModel, bool ffmpegAvailable, string? offlineMessage)
    {
        _scriptWriter = scriptWriter;
        _builder = builder;
        _grounding = grounding;
        _trendFeed = trendFeed;
        _visualsFor = visualsFor;
        _imageModel = imageModel;
        FfmpegAvailable = ffmpegAvailable;
        OfflineMessage = offlineMessage;
        Clips.CollectionChanged += OnClipsChanged;
    }

    public static string EpisodesRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PoAutoRobo");

    public ObservableCollection<ClipViewModel> Clips { get; } = [];

    /// <summary>Passages from the official repositories that the current script was grounded in.</summary>
    public ObservableCollection<GroundingSnippet> Snippets { get; } = [];

    public string SnippetsHeading => Snippets.Count == 0 ? "Repository inspector" : $"Repository inspector · {Snippets.Count} sources";

    public bool FfmpegAvailable { get; }

    public bool FfmpegMissing => !FfmpegAvailable;

    /// <summary>Which services are simulated and why; null when everything is live.</summary>
    public string? OfflineMessage { get; }

    public bool IsOffline => OfflineMessage is not null;

    public string? EpisodeFolder { get; private set; }

    /// <summary>Set by the view that owns the audio player.</summary>
    public Action<string>? PlayAudio { get; set; }

    /// <summary>Set by the view that owns the preview player; it must let go of the file before a new preview is written.</summary>
    public Action? ReleasePreview { get; set; }

    // ---- Wizard steps ----

    private static readonly string[] StepHints =
    [
        "Step 1 of 4 · Pick the one topic for this episode: type your own, adopt a story, or open an episode you saved earlier.",
        "Step 2 of 4 · Shape the script: drag clips into order, choose each clip's depth (A, B or C), then edit and audition the dialogue.",
        "Step 3 of 4 · Add the pictures: choose your host, set the visual mix, generate the pictures, or drop in your own video.",
        "Step 4 of 4 · Finish: choose the caption style, watch a preview, then render the master video.",
    ];

    /// <summary>Which page is showing: 0 Topic, 1 Script, 2 Pictures, 3 Export.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTopicStep), nameof(IsScriptStep), nameof(IsPicturesStep), nameof(IsExportStep), nameof(IsDeckStep), nameof(StepHint), nameof(NextLabel))]
    [NotifyCanExecuteChangedFor(nameof(NextStepCommand), nameof(PreviousStepCommand))]
    public partial int Step { get; set; }

    public bool IsTopicStep => Step == 0;
    public bool IsScriptStep => Step == 1;
    public bool IsPicturesStep => Step == 2;
    public bool IsExportStep => Step == 3;

    /// <summary>Script and Pictures both work on the clip deck.</summary>
    public bool IsDeckStep => Step is 1 or 2;

    public string StepHint => StepHints[Math.Clamp(Step, 0, 3)];

    public string NextLabel => Step switch { 0 => "Next: script", 1 => "Next: pictures", 2 => "Next: export", _ => "Next" };

    // The later pages have nothing to show until there is an episode.
    partial void OnStepChanged(int value)
    {
        if (value > 0 && !HasEpisode) Step = 0;
    }

    private bool CanGoNext => HasEpisode && Step < 3;

    private bool CanGoBack => Step > 0;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextStep() => Step++;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousStep() => Step--;

    // ---- Topic radar ----

    public ObservableCollection<TopicCard> Topics { get; } = [];

    [RelayCommand]
    private async Task RefreshTopicsAsync(CancellationToken ct)
    {
        var cards = await _trendFeed.GetAsync(ct); // never throws for a dead source; falls back to sample topics
        Topics.Clear();
        foreach (var card in cards)
            Topics.Add(card);
    }

    /// <summary>Takes a card as the episode's one topic and starts writing.</summary>
    [RelayCommand]
    private async Task AdoptTopicAsync(TopicCard card)
    {
        TopicInput = card.Summary.Length > 0 ? $"{card.Title}\n\n{card.Summary}" : card.Title;
        await CreateEpisodeCommand.ExecuteAsync(null);
    }

    // ---- Episode ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateEpisodeCommand))]
    public partial string TopicInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EpisodeTitle), nameof(RuntimeText), nameof(ClipCountText), nameof(HasEpisode), nameof(Captions),
        nameof(CaptionPresetIndex), nameof(CaptionFontSize), nameof(CaptionStroke), nameof(CaptionAccent))]
    [NotifyCanExecuteChangedFor(nameof(RenderCommand), nameof(BuildPreviewCommand), nameof(GenerateAllCommand), nameof(NextStepCommand))]
    public partial Episode? Episode { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public bool HasEpisode => Episode is not null;

    public string EpisodeTitle => Episode?.Title ?? "No episode yet";

    public string ClipCountText => Episode is null ? "" : Episode.Clips.Count == 1 ? "1 clip" : $"{Episode.Clips.Count} clips";

    public string RuntimeText
    {
        get
        {
            if (Episode is null) return "";
            var total = Durations.Total(Episode);
            return Durations.IsShort(total) ? $"Runtime {total:m\\:ss} · shorter than the 3 minute target" : $"Runtime {total:m\\:ss} · target 3–10 min";
        }
    }

    /// <summary>Saved episodes, newest first, for the Open list.</summary>
    public IReadOnlyList<string> SavedEpisodes() => ProjectStore.ListEpisodes(EpisodesRoot);

    /// <summary>Reopens a saved episode. Works with no connection: it is a plain file read.</summary>
    public async Task OpenEpisodeAsync(string folder)
    {
        ErrorMessage = null;
        if (IsWorking)
        {
            ErrorMessage = "Wait for the job in progress to finish, or cancel it, before opening another episode.";
            return;
        }
        try
        {
            Episode episode;
            try
            {
                episode = ProjectStore.Load(folder);
            }
            catch (InvalidDataException) when (File.Exists(Path.Combine(folder, ProjectStore.FileName + ".bak")))
            {
                if (Confirm is null || !await Confirm("This episode's file is damaged", "Restore it from the copy saved just before the last change?", "Restore"))
                    return;
                episode = ProjectStore.RestoreBackup(folder);
            }
            EpisodeFolder = folder;
            Snippets.Clear();
            OnPropertyChanged(nameof(SnippetsHeading));
            Show(episode);
            StatusMessage = $"Opened {episode.Title}.";
        }
        catch (InvalidDataException e)
        {
            ErrorMessage = e.Message;
        }
    }

    private static readonly EpisodeLength[] Lengths = [EpisodeLength.Full, EpisodeLength.Short, EpisodeLength.QuickTest];

    // Length of the next episode: 0 full (15 to 20 clips), 1 short (5 clips), 2 quick test (1 clip).
    [ObservableProperty]
    public partial int LengthIndex { get; set; }

    // Not while a long job runs: it works on the current episode and must not have it swapped from under it.
    private bool CanCreateEpisode => !string.IsNullOrWhiteSpace(TopicInput) && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanCreateEpisode))]
    private async Task CreateEpisodeAsync(CancellationToken ct)
    {
        ErrorMessage = null;
        try
        {
            var topic = TopicInput.Trim();
            var grounding = await _grounding.FindAsync(topic, ct);
            Snippets.Clear();
            foreach (var snippet in grounding)
                Snippets.Add(snippet);
            OnPropertyChanged(nameof(SnippetsHeading));
            var episode = VisualMix.Assign(await _scriptWriter.WriteEpisodeAsync(topic, grounding, Lengths[Math.Clamp(LengthIndex, 0, Lengths.Length - 1)], ct), MixPercentages.Default);
            EpisodeFolder = ProjectStore.NewFolder(EpisodesRoot, episode.Title); // never on top of an earlier episode
            ProjectStore.Save(episode, EpisodeFolder);
            Show(episode);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ErrorMessage = e.Message;
        }
    }

    /// <summary>Applies one edit, saves it, and refreshes only the cards whose clip changed.</summary>
    public void Edit(Func<Episode, Episode> change)
    {
        if (Episode is null || EpisodeFolder is null) return;
        try
        {
            var updated = change(Episode);
            var shownDialogue = SelectedClip?.Dialogue;
            ProjectStore.Save(updated, EpisodeFolder);
            Episode = updated;
            foreach (var card in Clips)
                card.Clip = updated.Clips.First(c => c.Id == card.Id);
            Renumber();
            // The dialogue box holds a copy of the selected clip's words. When an edit changes those words (a depth
            // switch, fitted footage), refresh the copy, or leaving the box would write the old words over the new.
            if (SelectedClip is { } selected && selected.Dialogue != shownDialogue)
                DraftDialogue = selected.Dialogue;
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            ErrorMessage = e.Message;
        }
    }

    private void Show(Episode episode)
    {
        _syncingClips = true;
        ReleasePreview?.Invoke();
        Preview = null;
        Episode = episode;
        Clips.Clear();
        foreach (var clip in episode.Clips)
            Clips.Add(new ClipViewModel(clip, Edit));
        _syncingClips = false;
        Renumber();
        SelectedClip = Clips.FirstOrDefault();
        Step = 1; // a new or reopened episode lands on its script
    }

    // A drag-reorder arrives as a remove followed by an insert; act once the deck is whole again.
    private void OnClipsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_syncingClips || Episode is null || Clips.Count != Episode.Clips.Count) return;
        Edit(episode => EpisodeEditor.Reorder(episode, [.. Clips.Select(c => c.Id)]));
    }

    private void Renumber()
    {
        for (var i = 0; i < Clips.Count; i++)
            Clips[i].Number = i + 1;
    }

    // ---- Selected clip ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(AuditionCommand), nameof(GeneratePictureCommand))]
    public partial ClipViewModel? SelectedClip { get; set; }

    /// <summary>The dialogue box's text while it is being typed; applied to the clip when the box loses focus.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftStats))]
    public partial string DraftDialogue { get; set; } = "";

    public bool HasSelection => SelectedClip is not null;

    public string DraftStats =>
        $"{DraftDialogue.Length} characters · about {Durations.Estimate(DraftDialogue).TotalSeconds:0} s";

    partial void OnSelectedClipChanged(ClipViewModel? value)
    {
        DraftDialogue = value?.Dialogue ?? "";
        FitMessage = null;
    }

    [RelayCommand]
    private async Task ApplyDialogueAsync(CancellationToken ct)
    {
        if (Episode is null || SelectedClip is not { } clip || DraftDialogue.Trim() == clip.Dialogue) return;
        try
        {
            var updated = await EpisodeEditor.EditDialogueAsync(Episode, clip.Id, DraftDialogue, _scriptWriter, ct);
            // The drift check above can take a moment; apply only this clip so edits made meanwhile are kept.
            var changed = updated.Clips.First(c => c.Id == clip.Id);
            Edit(e => EpisodeEditor.ReplaceClip(e, changed));
            await _builder.NarrateClipAsync(changed, EpisodeFolder!, ct); // new words are spoken straight away
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ErrorMessage = e.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task AuditionAsync(CancellationToken ct)
    {
        try
        {
            await ApplyDialogueAsync(ct);
            var narration = await _builder.NarrateClipAsync(SelectedClip!.Clip, EpisodeFolder!, ct);
            PlayAudio?.Invoke(narration.AudioPath);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ErrorMessage = e.Message;
        }
    }

    // ---- The user's own footage ----

    [ObservableProperty]
    public partial string? FitMessage { get; set; }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task AttachVideoAsync(string path, CancellationToken ct)
    {
        if (Episode is null || SelectedClip is not { } card) return;
        ErrorMessage = null;
        if (!FfmpegAvailable)
        {
            ErrorMessage = "FFmpeg is needed to read your video. Install it, then restart the app.";
            return;
        }
        FitMessage = "Fitting the narration to your video…";
        try
        {
            var clip = card.Clip;
            var length = await _builder.ProbeDurationAsync(path, ct);
            var fit = await Conformance.FitAsync(clip.Active.Dialogue, length, _scriptWriter,
                async (text, rate, c) => (await _builder.NarrateClipAsync(EpisodeEditor.WithDialogue(clip, text) with { NarrationRate = rate }, EpisodeFolder!, c)).Duration,
                ct);

            // Named for the clip as well as the file, so two different videos called "take.mp4" cannot overwrite each other.
            var copy = Path.Combine(EpisodeFolder!, "imports", $"{clip.Id:N}-{Path.GetFileName(path)}");
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            if (!string.Equals(Path.GetFullPath(path), copy, StringComparison.OrdinalIgnoreCase))
                File.Copy(path, copy, overwrite: true);

            Edit(e => EpisodeEditor.AttachVideo(e, clip.Id, copy, fit));
            FitMessage = fit.WithinTolerance
                ? $"Video {length:m\\:ss} · narration fitted to {fit.Duration.TotalSeconds:0.0} s"
                : $"Closest fit is {Math.Abs(fit.Gap.TotalSeconds):0.0} s too {(fit.Gap > TimeSpan.Zero ? "short" : "long")}. Edit the dialogue to close the gap.";
        }
        catch (OperationCanceledException)
        {
            FitMessage = null;
        }
        catch (Exception e) // the top of a user action: anything that went wrong is shown, never left to crash the app
        {
            FitMessage = null;
            ErrorMessage = e is ArgumentOutOfRangeException ? "Footage must be between 5 seconds and 2 minutes long." : e.Message;
        }
    }

    [RelayCommand]
    private void RemoveVideo()
    {
        if (SelectedClip is not { } card) return;
        Edit(e => EpisodeEditor.RemoveVideo(e, card.Id));
        FitMessage = null;
    }

    // ---- Pictures ----

    /// <summary>Set by the window: asks the user to approve something that costs money. True means go ahead.</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    public bool PicturesAvailable => _visualsFor is not null;

    /// <summary>Folder for host candidates made before any episode exists.</summary>
    private static string HostFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoAutoRobo", "host");

    public ObservableCollection<string> HostCandidates { get; } = [];

    private Visuals? CurrentVisuals => _visualsFor?.Invoke(EpisodeFolder ?? HostFolder);

    private bool CanGenerateAll => HasEpisode && PicturesAvailable && !IsWorking;

    private bool CanGeneratePicture => HasSelection && PicturesAvailable;

    /// <summary>Pictures are only ever made on an explicit request, and a batch only after the cost is confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerateAll), IncludeCancelCommand = true)]
    private async Task GenerateAllAsync(CancellationToken ct)
    {
        var estimate = CostEstimate.For(Episode!, _imageModel);
        ErrorMessage = null;
        if (estimate.NothingToDo)
        {
            StatusMessage = estimate.Summary;
            return;
        }
        if (Confirm is null || !await Confirm("Generate pictures?", estimate.Summary + " Pictures you already have are reused at no cost.", "Generate"))
            return;

        var failures = new List<string>();
        var made = 0;
        var total = estimate.ClipIds.Count;
        BeginActivity("Generating pictures");
        try
        {
            foreach (var id in estimate.ClipIds)
            {
                if (ct.IsCancellationRequested) break;
                var done = made + failures.Count;
                var title = Episode?.Clips.FirstOrDefault(c => c.Id == id)?.Title ?? "";
                ReportActivity($"Drawing the picture for clip {done + 1} of {total} · {title}", (double)done / total);
                if (await GenerateOneAsync(id, ct) is { } failure) failures.Add(failure); else made++;
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user part-way through a picture; that clip is not counted as made.
        }
        finally
        {
            EndActivity();
        }
        StatusMessage = ct.IsCancellationRequested ? $"Stopped. Pictures are ready for {made} clips." : $"Pictures are ready for {made} clips.";
        if (failures.Count > 0)
            ErrorMessage = $"{failures.Count} clips kept their title card. {failures[0]}";
    }

    [RelayCommand(CanExecute = nameof(CanGeneratePicture))]
    private async Task GeneratePictureAsync(CancellationToken ct)
    {
        ErrorMessage = null;
        try
        {
            if (SelectedClip is { } card && await GenerateOneAsync(card.Id, ct) is { } failure)
                ErrorMessage = failure;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <returns>Null on success, otherwise why this clip has no picture.</returns>
    /// <exception cref="OperationCanceledException">The user stopped it.</exception>
    private async Task<string?> GenerateOneAsync(Guid clipId, CancellationToken ct)
    {
        if (Episode?.Clips.FirstOrDefault(c => c.Id == clipId) is not { } clip)
            return "This clip is no longer in the episode.";
        try
        {
            var paths = await CurrentVisuals!.DrawAsync(clip, ct);
            // Attached to the clip as it is now: footage, a new picture type or changed words since the request win.
            Edit(e => EpisodeEditor.ApplyPicture(e, clip, paths));
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return "The picture service took too long to answer."; // a timeout, not the user: this clip was not made
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task MakeHostCandidatesAsync(CancellationToken ct)
    {
        ErrorMessage = null;
        try
        {
            var candidates = await CurrentVisuals!.CandidateSheetsAsync(3, ct);
            HostCandidates.Clear();
            foreach (var path in candidates)
                HostCandidates.Add(path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
    }

    /// <summary>Locks a picture (a candidate or the user's own file) as the host for every episode.</summary>
    public void LockHost(string path)
    {
        try
        {
            CurrentVisuals?.LockSheet(path);
            StatusMessage = "Host locked. New pictures will use this character.";
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
    }

    // ---- Visual mix ----

    public MixPercentages Mix => Episode?.Mix ?? MixPercentages.Default;

    public void ApplyMix(MixPercentages mix) => Edit(e => EpisodeEditor.SetMix(e, mix));

    public void RerollMix() => Edit(e => EpisodeEditor.RerollMix(e, Random.Shared.Next()));

    // ---- Captions ----

    public CaptionStyle Captions => Episode?.Captions ?? new CaptionStyle();

    public int CaptionPresetIndex
    {
        get => (int)Captions.Preset;
        set { if (value >= 0 && value != CaptionPresetIndex) SetCaptions(Captions with { Preset = (CaptionPreset)value }); }
    }

    public double CaptionFontSize
    {
        get => Captions.FontSize;
        set { if ((int)value != Captions.FontSize) SetCaptions(Captions with { FontSize = (int)value }); }
    }

    public double CaptionStroke
    {
        get => Captions.StrokeWidth;
        set { if ((int)value != Captions.StrokeWidth) SetCaptions(Captions with { StrokeWidth = (int)value }); }
    }

    public Color CaptionAccent
    {
        get => Color.FromArgb(255, Convert.ToByte(Captions.AccentColor[1..3], 16), Convert.ToByte(Captions.AccentColor[3..5], 16), Convert.ToByte(Captions.AccentColor[5..7], 16));
        set { if (value != CaptionAccent) SetCaptions(Captions with { AccentColor = $"#{value.R:X2}{value.G:X2}{value.B:X2}" }); }
    }

    // ponytail: every slider tick saves the episode file. It is a few KB; debounce here if it ever stutters.
    private void SetCaptions(CaptionStyle style) => Edit(e => e with { Captions = style });

    // ---- Preview and export ----

    [ObservableProperty]
    public partial Preview? Preview { get; set; }

    [ObservableProperty]
    public partial int ExportPresetIndex { get; set; }

    private bool CanRender => HasEpisode && FfmpegAvailable && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanRender), IncludeCancelCommand = true)]
    private async Task BuildPreviewAsync(CancellationToken ct)
    {
        await RunRenderAsync("Building the preview", "Preview ready.", async progress =>
        {
            ReleasePreview?.Invoke();
            Preview = null;
            Preview = await Task.Run(() => _builder.PreviewAsync(Episode!, EpisodeFolder!, progress, ct), ct);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRender), IncludeCancelCommand = true)]
    private async Task RenderAsync(CancellationToken ct)
    {
        var preset = ExportPresets[Math.Clamp(ExportPresetIndex, 0, ExportPresets.Length - 1)];
        string? output = null;
        await RunRenderAsync($"Rendering the master video ({preset.Height}p, {preset.Fps} fps)", null, async progress =>
            output = await Task.Run(() => _builder.ExportAsync(Episode!, EpisodeFolder!, preset, Captions, progress, ct), ct));
        if (output is not null)
            StatusMessage = $"Saved to {output}";
    }

    private async Task RunRenderAsync(string title, string? doneMessage, Func<IProgress<RenderProgress>, Task> work)
    {
        ErrorMessage = null;
        StatusMessage = null;
        BeginActivity(title);
        try
        {
            // Progress<T> hops back to the UI thread.
            await work(new Progress<RenderProgress>(p => ReportActivity(p.Activity, p.Fraction)));
            StatusMessage = doneMessage;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception e) // a render also narrates and reads files, so many things can fail; show them all
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            EndActivity();
        }
    }

    // ---- The one long job in progress ----

    private readonly System.Diagnostics.Stopwatch _activityClock = new();

    /// <summary>True while a preview, a render or a batch of pictures is running. Only one runs at a time.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenderCommand), nameof(BuildPreviewCommand), nameof(GenerateAllCommand), nameof(CancelActivityCommand), nameof(CreateEpisodeCommand))]
    public partial bool IsWorking { get; set; }

    /// <summary>What the job is, e.g. "Rendering the master video (1080p, 30 fps)".</summary>
    [ObservableProperty]
    public partial string ActivityTitle { get; set; } = "";

    /// <summary>The exact step it is on, e.g. "Drawing clip 7 of 16 · Rewards Shape Steps".</summary>
    [ObservableProperty]
    public partial string ActivityDetail { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityPercentText))]
    public partial double ActivityPercent { get; set; }

    /// <summary>Time spent and a rough time left, worked out from the pace so far.</summary>
    [ObservableProperty]
    public partial string ActivityTime { get; set; } = "";

    public string ActivityPercentText => $"{ActivityPercent:0}%";

    private void BeginActivity(string title)
    {
        ActivityTitle = title;
        ActivityDetail = "Starting…";
        ActivityPercent = 0;
        ActivityTime = "";
        _activityClock.Restart();
        IsWorking = true;
    }

    private void ReportActivity(string detail, double fraction)
    {
        if (!IsWorking) return; // a late report after the job ended
        ActivityDetail = detail;
        ActivityPercent = Math.Clamp(fraction, 0, 1) * 100;
        var elapsed = _activityClock.Elapsed;
        var left = fraction > 0.03 ? TimeSpan.FromTicks((long)(elapsed.Ticks * (1 - fraction) / fraction)) : (TimeSpan?)null;
        ActivityTime = left is { } remaining
            ? $"{elapsed:m\\:ss} so far · {(remaining.TotalMinutes >= 1 ? $"about {Math.Ceiling(remaining.TotalMinutes):0} min" : "under a minute")} left"
            : $"{elapsed:m\\:ss} so far";
    }

    private void EndActivity()
    {
        _activityClock.Stop();
        IsWorking = false;
    }

    /// <summary>Stops whichever long job is running. Finished pictures and narration are kept.</summary>
    [RelayCommand(CanExecute = nameof(IsWorking))]
    private void CancelActivity()
    {
        ActivityDetail = "Stopping…";
        RenderCancelCommand.Execute(null);
        BuildPreviewCancelCommand.Execute(null);
        GenerateAllCancelCommand.Execute(null);
    }
}