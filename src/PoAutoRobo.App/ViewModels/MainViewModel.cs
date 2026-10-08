using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;
using Windows.UI;

namespace PoAutoRobo.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
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
        var prefs = Prefs.Load();
        LengthIndex = Math.Clamp(prefs.LengthIndex, 0, LengthChoices.Length - 1);
        ExportPresetIndex = Math.Clamp(prefs.ExportPresetIndex, 0, ExportChoices.Length - 1);
        SoundsOn = prefs.SoundsOn;
        Clips.CollectionChanged += OnClipsChanged;
        RefreshLibrary();
    }

    public static string EpisodesRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PoAutoRobo");

    // The lists behind the drop-downs. Each entry carries its own label, so adding a choice is one line here.
    public Choice<EpisodeLength>[] LengthChoices { get; } =
    [
        new("Full episode · 15 to 20 clips, about 10 minutes", EpisodeLength.Full),
        new("Short · 5 clips, about 3 minutes", EpisodeLength.Short),
        new("Two clips · about 1 minute", EpisodeLength.TwoClips),
        new("Quick test · 1 clip, about 30 seconds", EpisodeLength.QuickTest),
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

    /// <summary>Set by the window: asks the user to approve something that costs money or cannot be undone. True means go ahead.</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Set by the window: told the job's name and whether it finished, when a long job ends.</summary>
    public Action<string, bool>? JobFinished { get; set; }

    // ---- What the app remembers between runs ----

    /// <summary>Length of the next episode, as an index into <see cref="LengthChoices"/>.</summary>
    [ObservableProperty]
    public partial int LengthIndex { get; set; }

    [ObservableProperty]
    public partial int ExportPresetIndex { get; set; }

    [ObservableProperty]
    public partial bool SoundsOn { get; set; }

    partial void OnLengthIndexChanged(int value) => SavePrefs();

    partial void OnExportPresetIndexChanged(int value) => SavePrefs();

    partial void OnSoundsOnChanged(bool value) => SavePrefs();

    private void SavePrefs() => new Prefs(LengthIndex, ExportPresetIndex, SoundsOn).Save();

    // ---- Steps ----

    private static readonly string[] StepHints =
    [
        "Pick the one topic for this episode: type your own, adopt a story, or open an episode you saved earlier.",
        "Shape the script: drag clips into order, choose each clip's depth, then edit and audition the dialogue.",
        "Add the pictures: choose your host, set the visual mix, generate the pictures, or drop in your own video.",
        "Finish: choose the caption style, watch a preview, then render the master video.",
    ];

    /// <summary>Which page is showing: 0 Topic, 1 Script, 2 Pictures, 3 Export.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTopic), nameof(IsScriptStep), nameof(IsPicturesStep), nameof(IsExportStep), nameof(ShowDeck), nameof(StepHint))]
    public partial int Step { get; set; }

    public bool IsScriptStep => Step == 1;
    public bool IsPicturesStep => Step == 2;
    public bool IsExportStep => Step == 3;

    // While a script is being written the deck page shows in place of the topic page, with placeholder cards.
    public bool ShowTopic => Step == 0 && !IsCreating;

    /// <summary>Script and Pictures both work on the clip deck.</summary>
    public bool ShowDeck => Step is 1 or 2 || IsCreating;

    public string StepHint => StepHints[Math.Clamp(Step, 0, 3)];

    partial void OnStepChanged(int value)
    {
        if (value > 0 && !HasEpisode) Step = 0; // the later pages have nothing to show until there is an episode
        if (value == 0) RefreshLibrary();
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
        var cards = await _trendFeed.GetAsync(ct); // never throws for a dead source; falls back to sample topics
        Topics.Clear();
        foreach (var card in cards)
            Topics.Add(card);
    }

    /// <summary>Takes a card as the episode's one topic and starts writing.</summary>
    [RelayCommand]
    private async Task AdoptTopicAsync(TopicCard card)
    {
        // Running a command directly skips its own "can this run now" check, so it is asked here: without it a
        // second click, or a click during a render, would start (and pay for) a second script.
        if (IsWorking) return;
        TopicInput = card.Summary.Length > 0 ? $"{card.Title}\n\n{card.Summary}" : card.Title;
        if (CreateEpisodeCommand.CanExecute(null))
            await CreateEpisodeCommand.ExecuteAsync(null);
    }

    // ---- Saved episodes ----

    public ObservableCollection<EpisodeSummary> Library { get; } = [];

    public bool HasLibrary => Library.Count > 0;

    public void RefreshLibrary()
    {
        Library.Clear();
        foreach (var summary in ProjectStore.Summaries(EpisodesRoot))
            Library.Add(summary);
        OnPropertyChanged(nameof(HasLibrary));
    }

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
            ShowSnippets(ProjectStore.LoadSnippets(folder));
            Show(episode);
            StatusMessage = $"Opened {episode.Title}.";
        }
        catch (InvalidDataException e)
        {
            ErrorMessage = e.Message;
        }
    }

    public void DuplicateEpisode(EpisodeSummary summary) => _ = Guard(() =>
    {
        ProjectStore.Duplicate(summary.Folder, EpisodesRoot);
        RefreshLibrary();
        StatusMessage = $"Copied {summary.Title}.";
        return Task.CompletedTask;
    });

    public static void ShowInExplorer(string folder) => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } })?.Dispose();

    /// <summary>Moves an episode folder to the Recycle Bin, after asking. The episode in use can only go when no job is running.</summary>
    public Task DeleteEpisodeAsync(EpisodeSummary summary) => Guard(async () =>
    {
        var isOpen = string.Equals(summary.Folder, EpisodeFolder, StringComparison.OrdinalIgnoreCase);
        if (isOpen && IsWorking)
        {
            ErrorMessage = "Wait for the job in progress to finish, or cancel it, before deleting this episode.";
            return;
        }
        if (Confirm is null || !await Confirm($"Delete {summary.Title}?", "The episode's folder, with its script, voices, pictures and finished videos, goes to the Recycle Bin.", "Delete"))
            return;
        if (isOpen)
        {
            ReleasePreview?.Invoke();
            Preview = null;
            _syncingClips = true;
            Clips.Clear();
            _syncingClips = false;
            SelectedClip = null;
            Episode = null;
            EpisodeFolder = null;
            ForgetHistory();
            Step = 0;
        }
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(summary.Folder,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        RefreshLibrary();
        StatusMessage = $"Moved {summary.Title} to the Recycle Bin.";
    });

    // ---- Episode ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateEpisodeCommand))]
    public partial string TopicInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EpisodeTitle), nameof(MetaText), nameof(HasEpisode), nameof(Captions),
        nameof(CaptionPresetIndex), nameof(CaptionFontSize), nameof(CaptionStroke), nameof(CaptionAccent))]
    [NotifyCanExecuteChangedFor(nameof(RenderCommand), nameof(BuildPreviewCommand), nameof(GenerateAllCommand), nameof(NarrateAllCommand), nameof(CleanMediaCommand), nameof(ShowEpisodeFolderCommand))]
    public partial Episode? Episode { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>The outcome of the last action, shown on every step.</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public bool HasEpisode => Episode is not null;

    public string EpisodeTitle => Episode?.Title ?? "No episode yet";

    /// <summary>The line under the title: clip count, running time and what has been spent.</summary>
    public string MetaText
    {
        get
        {
            if (Episode is null || EpisodeFolder is null) return "";
            var total = TimeSpan.FromTicks(Clips.Sum(c => c.Duration.Ticks));
            var spent = SpendLog.Total(EpisodeFolder);
            return string.Join(" · ", new[]
            {
                Episode.Clips.Count == 1 ? "1 clip" : $"{Episode.Clips.Count} clips",
                Durations.IsShort(total) ? $"runtime {total:m\\:ss}, shorter than the 3 minute target" : $"runtime {total:m\\:ss}",
                spent > 0 ? "spent " + SpendLog.InWords(spent) : "",
            }.Where(part => part.Length > 0));
        }
    }

    /// <summary>True while a script is being written; the deck shows placeholder cards meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTopic), nameof(ShowDeck))]
    public partial bool IsCreating { get; set; }

    /// <summary>One placeholder per clip the script being written may have.</summary>
    public int[] Skeletons => [.. Enumerable.Range(0, LengthChoices[LengthIndex].Value.MaxClips)];

    // Not while a long job runs: it works on the current episode and must not have it swapped from under it.
    private bool CanCreateEpisode => !string.IsNullOrWhiteSpace(TopicInput) && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanCreateEpisode))]
    private Task CreateEpisodeAsync() => RunActivityAsync("Writing the script", async ct =>
    {
        var topic = TopicInput.Trim();
        var length = LengthChoices[LengthIndex].Value;
        OnPropertyChanged(nameof(Skeletons));
        IsCreating = true;
        try
        {
            ReportActivity("Reading the official repositories", 0.02);
            var grounding = await _grounding.FindAsync(topic, ct);
            ReportActivity("Writing the script", 0.1);
            var of = length.MinClips == length.MaxClips ? $"{length.MaxClips}" : $"up to {length.MaxClips}";
            var written = new Progress<int>(n => ReportActivity($"Written {n} of {of} clips", 0.1 + 0.9 * Math.Min(1.0, (double)n / length.MaxClips)));
            var episode = VisualMix.Assign(await _scriptWriter.WriteEpisodeAsync(topic, grounding, length, ct, written), MixPercentages.Default);
            EpisodeFolder = ProjectStore.NewFolder(EpisodesRoot, episode.Title); // never on top of an earlier episode
            ProjectStore.Save(episode, EpisodeFolder);
            ProjectStore.SaveSnippets(grounding, EpisodeFolder);
            ShowSnippets(grounding);
            Show(episode);
        }
        finally
        {
            IsCreating = false;
        }
    });

    private void ShowSnippets(IReadOnlyList<GroundingSnippet> snippets)
    {
        Snippets.Clear();
        foreach (var snippet in snippets)
            Snippets.Add(snippet);
        OnPropertyChanged(nameof(SnippetsHeading));
    }

    private void Show(Episode episode)
    {
        _syncingClips = true;
        ReleasePreview?.Invoke();
        Preview = null;
        LastExport = null;
        ForgetHistory();
        Episode = episode;
        Clips.Clear();
        foreach (var clip in episode.Clips)
            Clips.Add(new ClipViewModel(clip, Edit, PickTier));
        _syncingClips = false;
        SyncCards();
        SelectedClip = Clips.FirstOrDefault();
        Step = 1; // a new or reopened episode lands on its script
    }

    // ---- Edits, with undo ----

    private const int HistoryKept = 50;
    private readonly List<Episode> _undo = [];
    private readonly List<Episode> _redo = [];
    private bool _lastEditWasCaptions;

    /// <summary>Applies one edit, saves it, and refreshes only the cards whose clip changed.</summary>
    public void Edit(Func<Episode, Episode> change)
    {
        if (Episode is not { } before || EpisodeFolder is null) return;
        try
        {
            var updated = change(before);
            if (ReferenceEquals(updated, before)) return;
            // Dragging a caption slider is dozens of edits in a row; they are undone as one.
            var captionsOnly = updated with { Captions = before.Captions } == before;
            if (!(captionsOnly && _lastEditWasCaptions))
            {
                _undo.Add(before);
                if (_undo.Count > HistoryKept) _undo.RemoveAt(0);
            }
            _lastEditWasCaptions = captionsOnly;
            _redo.Clear();
            Apply(updated);
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            ErrorMessage = e.Message;
        }
    }

    private bool CanUndo => _undo.Count > 0;

    private bool CanRedo => _redo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Travel(_undo, _redo);

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Travel(_redo, _undo);

    private void Travel(List<Episode> from, List<Episode> to)
    {
        if (Episode is null || from.Count == 0) return;
        try
        {
            var target = from[^1];
            from.RemoveAt(from.Count - 1);
            to.Add(Episode);
            _lastEditWasCaptions = false;
            Apply(target);
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
    }

    private void ForgetHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _lastEditWasCaptions = false;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Makes <paramref name="updated"/> the episode: saves it and brings the cards into line, order included.</summary>
    private void Apply(Episode updated)
    {
        var shownDialogue = SelectedClip?.Dialogue;
        var selected = SelectedClip;
        ProjectStore.Save(updated, EpisodeFolder!);
        Episode = updated;

        _syncingClips = true;
        for (var i = 0; i < updated.Clips.Count; i++)
        {
            var at = i;
            while (Clips[at].Id != updated.Clips[i].Id) at++;
            if (at != i) Clips.Move(at, i); // only an undone or redone reorder moves anything
            Clips[i].Clip = updated.Clips[i];
        }
        _syncingClips = false;
        if (SelectedClip != selected) SelectedClip = selected; // a move can drop the selection in the list control

        SyncCards();
        // The dialogue box holds a copy of the selected clip's words. When an edit changes those words (a depth
        // switch, fitted footage), refresh the copy, or leaving the box would write the old words over the new.
        if (SelectedClip is { } card && card.Dialogue != shownDialogue)
            DraftDialogue = card.Dialogue;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Renumbers the cards and refreshes what they show beyond the clip itself: recorded length and unchecked figures.</summary>
    private void SyncCards()
    {
        if (Episode is null || EpisodeFolder is null) return;
        // With no sources there is nothing to check against, so nothing is flagged.
        // A repository's own name and file paths count as sourced, as does anything the user wrote in the topic.
        var sources = Snippets.Count == 0 ? null : Snippets.SelectMany(s => new[] { s.Text, s.Repo, s.Path }).Append(Episode.Topic).ToList();
        for (var i = 0; i < Clips.Count; i++)
        {
            var card = Clips[i];
            card.Number = i + 1;
            card.Measured = _builder.MeasuredDuration(card.Clip, EpisodeFolder);
            card.Unverified = sources is not null && Grounding.UnverifiedClaims(card.Dialogue, sources) is { Count: > 0 } claims
                ? "Not in the sources: " + string.Join(", ", claims)
                : null;
        }
        OnPropertyChanged(nameof(MetaText));
    }

    // A drag-reorder arrives as a remove followed by an insert; act once the deck is whole again.
    private void OnClipsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_syncingClips || Episode is null || Clips.Count != Episode.Clips.Count) return;
        Edit(episode => EpisodeEditor.Reorder(episode, [.. Clips.Select(c => c.Id)]));
    }

    // Reordering without a drag: for keyboard and screen-reader use, and as a dependable alternative to dragging.
    // Moving the card raises the same collection change a drag does, so the one reorder path handles both.
    [RelayCommand]
    private void MoveEarlier() => MoveSelected(-1);

    [RelayCommand]
    private void MoveLater() => MoveSelected(1);

    private void MoveSelected(int by)
    {
        if (SelectedClip is not { } card) return;
        var from = Clips.IndexOf(card);
        var to = from + by;
        if (from < 0 || to < 0 || to >= Clips.Count) return;
        Clips.Move(from, to);
        SelectedClip = card; // a move can drop the selection in the list control
    }

    /// <summary>Switches a clip's depth. A depth the clip does not have yet is written first, which takes a few seconds.</summary>
    private async void PickTier(ClipViewModel card, Tier tier)
    {
        if (Episode is not { } episode) return;
        if (card.Clip.Scripts.ContainsKey(tier))
        {
            Edit(e => EpisodeEditor.SetTier(e, card.Id, tier));
            return;
        }
        card.IsBusy = true;
        await Guard(async () =>
        {
            var script = await _scriptWriter.WriteTierAsync(episode.Topic, card.Clip, tier, CancellationToken.None);
            // The episode may have been closed or swapped while the depth was being written.
            Edit(e => e.Clips.Any(c => c.Id == card.Id) ? EpisodeEditor.SetTier(EpisodeEditor.AddTier(e, card.Id, tier, script), card.Id, tier) : e);
        });
        card.IsBusy = false;
        card.ResetTierSelector();
    }

    // ---- Selected clip ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(DraftProblem), nameof(HasDraftProblem))]
    [NotifyCanExecuteChangedFor(nameof(AuditionCommand), nameof(GeneratePictureCommand))]
    public partial ClipViewModel? SelectedClip { get; set; }

    /// <summary>The dialogue box's text while it is being typed; applied to the clip when the box loses focus.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftStats), nameof(DraftProblem), nameof(HasDraftProblem))]
    public partial string DraftDialogue { get; set; } = "";

    public bool HasSelection => SelectedClip is not null;

    public string DraftStats =>
        $"{DraftDialogue.Length} characters · about {Durations.Estimate(DraftDialogue).TotalSeconds:0} s";

    /// <summary>What is wrong with the words as typed, shown under the box while typing; null when they are fine.</summary>
    public string? DraftProblem =>
        !HasSelection ? null : DraftDialogue.Trim().Length == 0 ? "Dialogue cannot be empty. The clip keeps its last words." : Durations.Warning(DraftDialogue);

    public bool HasDraftProblem => DraftProblem is not null;

    partial void OnSelectedClipChanged(ClipViewModel? value) => DraftDialogue = value?.Dialogue ?? "";

    // Selecting another clip reloads the dialogue box, and the click that selects it arrives before the box reports
    // losing focus. Words typed for the clip being left are saved here first, or they would be thrown away.
    partial void OnSelectedClipChanging(ClipViewModel? oldValue, ClipViewModel? newValue)
    {
        if (!_syncingClips && oldValue is not null && Episode is not null && Episode.Clips.Any(c => c.Id == oldValue.Id))
            _ = ApplyDialogueToAsync(oldValue, DraftDialogue, CancellationToken.None);
    }

    [RelayCommand]
    private Task ApplyDialogueAsync(CancellationToken ct) =>
        SelectedClip is { } clip ? ApplyDialogueToAsync(clip, DraftDialogue, ct) : Task.CompletedTask;

    private Task ApplyDialogueToAsync(ClipViewModel clip, string draft, CancellationToken ct) => Guard(async () =>
    {
        // Empty words are not applied; the box says so underneath.
        if (Episode is null || EpisodeFolder is not { } folder || draft.Trim().Length == 0 || draft.Trim() == clip.Dialogue) return;
        var updated = await EpisodeEditor.EditDialogueAsync(Episode, clip.Id, draft, _scriptWriter, ct);
        // The drift check above can take a moment; apply only this clip so edits made meanwhile are kept.
        var changed = updated.Clips.First(c => c.Id == clip.Id);
        Edit(e => EpisodeEditor.ReplaceClip(e, changed));
        await _builder.NarrateClipAsync(changed, folder, ct); // new words are spoken straight away
        SyncCards();
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task AuditionAsync(CancellationToken ct) => Guard(async () =>
    {
        await ApplyDialogueAsync(ct);
        var narration = await _builder.NarrateClipAsync(SelectedClip!.Clip, EpisodeFolder!, ct);
        SyncCards();
        PlayAudio?.Invoke(narration.AudioPath);
    });

    private bool CanStartJob => HasEpisode && !IsWorking;

    /// <summary>Records every clip's voice now, so the cards and the running time show real lengths before any render.</summary>
    [RelayCommand(CanExecute = nameof(CanStartJob))]
    private Task NarrateAllAsync() => RunActivityAsync("Recording the voices", async ct =>
    {
        var folder = EpisodeFolder!;
        var cards = Clips.ToList();
        for (var i = 0; i < cards.Count; i++)
        {
            ReportActivity($"Recording clip {i + 1} of {cards.Count} · {cards[i].Clip.Title}", (double)i / cards.Count);
            cards[i].IsBusy = true;
            try
            {
                await _builder.NarrateClipAsync(cards[i].Clip, folder, ct);
            }
            finally
            {
                cards[i].IsBusy = false;
            }
            SyncCards();
        }
    }, "Voices recorded. Clip lengths and the running time are now measured, not estimated.");

    // ---- The user's own footage ----

    [RelayCommand]
    private Task AttachVideoAsync(string path)
    {
        if (Episode is null || SelectedClip is not { } card) return Task.CompletedTask;
        if (IsWorking || !FfmpegAvailable)
        {
            ErrorMessage = IsWorking
                ? "Wait for the job in progress to finish, or cancel it, before adding your video."
                : "FFmpeg is needed to read your video. Install it, then restart the app.";
            return Task.CompletedTask;
        }
        return RunActivityAsync("Fitting the narration to your video", async ct =>
        {
            var (clip, folder) = (card.Clip, EpisodeFolder!);
            var length = await _builder.ProbeDurationAsync(path, ct);
            if (length < Conformance.MinFootage || length > Conformance.MaxFootage)
                throw new InvalidOperationException("Footage must be between 5 seconds and 2 minutes long.");
            ReportActivity("Rewriting and re-recording the dialogue to match", 0.2);
            var fit = await Conformance.FitAsync(clip.Active.Dialogue, length, _scriptWriter,
                async (text, rate, c) => (await _builder.NarrateClipAsync(EpisodeEditor.WithDialogue(clip, text) with { NarrationRate = rate }, folder, c)).Duration,
                ct);

            // Named for the clip as well as the file, so two different videos called "take.mp4" cannot overwrite each other.
            ReportActivity("Copying your video into the episode", 0.9);
            var copy = Path.Combine(folder, "imports", $"{clip.Id:N}-{Path.GetFileName(path)}");
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            if (!string.Equals(Path.GetFullPath(path), copy, StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => File.Copy(path, copy, overwrite: true), ct); // can be hundreds of megabytes

            Edit(e => EpisodeEditor.AttachVideo(e, clip.Id, copy, fit));
            StatusMessage = fit.WithinTolerance
                ? $"Video {length:m\\:ss} · narration fitted to {fit.Duration.TotalSeconds:0.0} s."
                : $"Closest fit is {Math.Abs(fit.Gap.TotalSeconds):0.0} s too {(fit.Gap > TimeSpan.Zero ? "short" : "long")}. Edit the dialogue to close the gap.";
        });
    }

    [RelayCommand]
    private void RemoveVideo()
    {
        if (SelectedClip is { } card)
            Edit(e => EpisodeEditor.RemoveVideo(e, card.Id));
    }

    // ---- Pictures ----

    public bool PicturesAvailable => _visualsFor is not null;

    /// <summary>Folder for host candidates made before any episode exists.</summary>
    private static string HostFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoAutoRobo", "host");

    public ObservableCollection<string> HostCandidates { get; } = [];

    private Visuals? CurrentVisuals => _visualsFor?.Invoke(EpisodeFolder ?? HostFolder);

    private bool CanGenerateAll => HasEpisode && PicturesAvailable && !IsWorking;

    // Not during a batch: the batch may be drawing this very clip, and the same request sent twice is paid for twice.
    private bool CanGeneratePicture => HasSelection && PicturesAvailable && !IsWorking;

    /// <summary>Pictures are only ever made on an explicit request, and a batch only after the cost is confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerateAll))]
    private async Task GenerateAllAsync()
    {
        var estimate = CostEstimate.For(Episode!, _imageModel);
        ErrorMessage = null;
        if (estimate.NothingToDo)
        {
            StatusMessage = estimate.Summary;
            return;
        }
        if (Confirm is null || !await Confirm("Generate pictures?", estimate.Summary + " Pictures you already have are reused at no cost.", "Generate") || IsWorking)
            return;

        var failures = new List<string>();
        var made = 0;
        var total = estimate.ClipIds.Count;
        var finished = await RunActivityAsync("Generating pictures", async ct =>
        {
            foreach (var id in estimate.ClipIds)
            {
                ct.ThrowIfCancellationRequested();
                var done = made + failures.Count;
                var title = Episode?.Clips.FirstOrDefault(c => c.Id == id)?.Title ?? "";
                ReportActivity($"Drawing the picture for clip {done + 1} of {total} · {title}", (double)done / total);
                if (await GenerateOneAsync(id, ct) is { } failure) failures.Add(failure); else made++;
            }
        });
        // A clip that fails is recorded above and never stops the batch, so "not finished" here means stopped by the user.
        StatusMessage = finished ? $"Pictures are ready for {made} clips." : $"Stopped. Pictures are ready for {made} clips.";
        if (failures.Count > 0)
            ErrorMessage = $"{failures.Count} clips kept their title card. {failures[0]}";
    }

    [RelayCommand(CanExecute = nameof(CanGeneratePicture))]
    private Task GeneratePictureAsync(CancellationToken ct)
    {
        ErrorMessage = null;
        return Guard(async () =>
        {
            if (SelectedClip is { } card && await GenerateOneAsync(card.Id, ct) is { } failure)
                ErrorMessage = failure;
        });
    }

    /// <returns>Null on success, otherwise why this clip has no picture.</returns>
    /// <exception cref="OperationCanceledException">The user stopped it.</exception>
    private async Task<string?> GenerateOneAsync(Guid clipId, CancellationToken ct)
    {
        if (Episode?.Clips.FirstOrDefault(c => c.Id == clipId) is not { } clip)
            return "This clip is no longer in the episode.";
        var card = Clips.FirstOrDefault(c => c.Id == clipId);
        card?.IsBusy = true;
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
        finally
        {
            card?.IsBusy = false;
            OnPropertyChanged(nameof(MetaText)); // a new picture adds to what has been spent
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private Task MakeHostCandidatesAsync(CancellationToken ct)
    {
        ErrorMessage = null;
        return Guard(async () =>
        {
            var candidates = await CurrentVisuals!.CandidateSheetsAsync(3, ct);
            HostCandidates.Clear();
            foreach (var path in candidates)
                HostCandidates.Add(path);
        });
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

    // ---- Housekeeping ----

    [RelayCommand(CanExecute = nameof(HasEpisode))]
    private void ShowEpisodeFolder() => ShowInExplorer(EpisodeFolder!);

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

    // ---- Captions ----

    public CaptionStyle Captions => Episode?.Captions ?? new CaptionStyle();

    public int CaptionPresetIndex
    {
        get => Array.FindIndex(CaptionChoices, c => c.Value == Captions.Preset);
        set { if (value >= 0 && value < CaptionChoices.Length && value != CaptionPresetIndex) SetCaptions(Captions with { Preset = CaptionChoices[value].Value }); }
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

    /// <summary>The master video most recently rendered for this episode, for the "Show in folder" button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExport))]
    [NotifyCanExecuteChangedFor(nameof(ShowExportCommand))]
    public partial string? LastExport { get; set; }

    public bool HasExport => LastExport is not null;

    [RelayCommand(CanExecute = nameof(HasExport))]
    private void ShowExport() => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LastExport}\""))?.Dispose();

    private bool CanRender => HasEpisode && FfmpegAvailable && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanRender))]
    private Task BuildPreviewAsync() => RunActivityAsync("Building the preview", async ct =>
    {
        ReleasePreview?.Invoke();
        Preview = null;
        var (episode, folder, progress) = (Episode!, EpisodeFolder!, RenderProgress());
        Preview = await Task.Run(() => _builder.PreviewAsync(episode, folder, progress, ct), ct);
        SyncCards(); // every clip has now been recorded
    }, "Preview ready.");

    [RelayCommand(CanExecute = nameof(CanRender))]
    private Task RenderAsync()
    {
        var preset = ExportChoices[ExportPresetIndex].Value;
        return RunActivityAsync($"Rendering the master video ({preset.Height}p, {preset.Fps} fps)", async ct =>
        {
            var (episode, folder, captions, progress) = (Episode!, EpisodeFolder!, Captions, RenderProgress());
            LastExport = await Task.Run(() => _builder.ExportAsync(episode, folder, preset, captions, progress, ct), ct);
            SyncCards();
            StatusMessage = $"Saved {Path.GetFileName(LastExport)}, with its chapter list, subtitles and thumbnail beside it.";
        });
    }

    // Progress<T> hops back to the UI thread, so it must be made on it.
    private Progress<RenderProgress> RenderProgress() => new(p => ReportActivity(p.Activity, p.Fraction));

    // ---- The one long job in progress ----

    private readonly Stopwatch _activityClock = new();
    private CancellationTokenSource? _cancel;

    /// <summary>True while a script, a recording pass, a preview, a render or a batch of pictures is running. Only one runs at a time.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenderCommand), nameof(BuildPreviewCommand), nameof(GenerateAllCommand), nameof(GeneratePictureCommand),
        nameof(NarrateAllCommand), nameof(CleanMediaCommand), nameof(CancelActivityCommand), nameof(CreateEpisodeCommand))]
    public partial bool IsWorking { get; set; }

    /// <summary>What the job is, e.g. "Rendering the master video (1080p, 30 fps)".</summary>
    [ObservableProperty]
    public partial string ActivityTitle { get; set; } = "";

    /// <summary>The exact step it is on, e.g. "Drawing clip 7 of 16 · Rewards Shape Steps".</summary>
    [ObservableProperty]
    public partial string ActivityDetail { get; set; } = "";

    [ObservableProperty]
    public partial double ActivityPercent { get; set; }

    /// <summary>Time spent and a rough time left, worked out from the pace so far.</summary>
    [ObservableProperty]
    public partial string ActivityTime { get; set; } = "";

    /// <summary>
    /// Runs the one long job: shows it in the progress panel, lets Cancel stop it, and turns anything that goes
    /// wrong into a message instead of a crash.
    /// </summary>
    /// <returns>True when the work ran to the end; false when it was stopped or failed.</returns>
    private async Task<bool> RunActivityAsync(string title, Func<CancellationToken, Task> work, string? doneMessage = null)
    {
        ErrorMessage = null;
        StatusMessage = null;
        using var cancel = _cancel = new CancellationTokenSource();
        ActivityTitle = title;
        ActivityDetail = "Starting…";
        ActivityPercent = 0;
        ActivityTime = "";
        _activityClock.Restart();
        IsWorking = true;
        var finished = false;
        try
        {
            await work(cancel.Token);
            StatusMessage ??= doneMessage;
            finished = true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Stopped.";
        }
        catch (Exception e) // the top of a user action, and a job narrates, draws and reads files, so many things can fail
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            _activityClock.Stop();
            _cancel = null;
            IsWorking = false;
        }
        JobFinished?.Invoke(title, finished);
        return finished;
    }

    /// <summary>Runs a short user action; anything that goes wrong is shown, never left to crash the app.</summary>
    private async Task Guard(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user, or overtaken by a newer request: nothing to report.
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
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

    /// <summary>Stops whichever long job is running. Finished pictures and narration are kept.</summary>
    [RelayCommand(CanExecute = nameof(IsWorking))]
    private void CancelActivity()
    {
        ActivityDetail = "Stopping…";
        _cancel?.Cancel();
    }
}
