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
    private bool _syncingClips;

    public MainViewModel(IScriptWriter scriptWriter, EpisodeBuilder builder, Grounding grounding, bool ffmpegAvailable, string? offlineMessage)
    {
        _scriptWriter = scriptWriter;
        _builder = builder;
        _grounding = grounding;
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

    // ---- Episode ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateEpisodeCommand))]
    public partial string TopicInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EpisodeTitle), nameof(RuntimeText), nameof(ClipCountText), nameof(HasEpisode), nameof(Captions),
        nameof(CaptionPresetIndex), nameof(CaptionFontSize), nameof(CaptionStroke), nameof(CaptionAccent))]
    [NotifyCanExecuteChangedFor(nameof(RenderCommand), nameof(BuildPreviewCommand))]
    public partial Episode? Episode { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public bool HasEpisode => Episode is not null;

    public string EpisodeTitle => Episode?.Title ?? "No episode yet";

    public string ClipCountText => Episode is null ? "" : $"{Episode.Clips.Count} clips";

    public string RuntimeText
    {
        get
        {
            if (Episode is null) return "";
            var total = TimeSpan.FromTicks(Episode.Clips.Sum(c => Durations.Estimate(c.Active.Dialogue).Ticks));
            return $"Runtime {total:m\\:ss} · target 3–10 min";
        }
    }

    private bool CanCreateEpisode => !string.IsNullOrWhiteSpace(TopicInput);

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
            var episode = await _scriptWriter.WriteEpisodeAsync(topic, grounding, ct);
            EpisodeFolder = Path.Combine(EpisodesRoot, EpisodeBuilder.Slug(episode.Title));
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
            ProjectStore.Save(updated, EpisodeFolder);
            Episode = updated;
            foreach (var card in Clips)
                card.Clip = updated.Clips.First(c => c.Id == card.Id);
            Renumber();
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
    [NotifyCanExecuteChangedFor(nameof(AuditionCommand))]
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
            Edit(_ => updated);
            await _builder.NarrateClipAsync(clip.Clip, EpisodeFolder!, ct); // new words are spoken straight away
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
        FitMessage = "Fitting the narration to your video…";
        try
        {
            var clip = card.Clip;
            var length = await _builder.ProbeDurationAsync(path, ct);
            var fit = await Conformance.FitAsync(clip.Active.Dialogue, length, _scriptWriter,
                async (text, rate, c) => (await _builder.NarrateClipAsync(EpisodeEditor.WithDialogue(clip, text) with { NarrationRate = rate }, EpisodeFolder!, c)).Duration,
                ct);

            var copy = Path.Combine(EpisodeFolder!, "imports", Path.GetFileName(path));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            if (!string.Equals(Path.GetFullPath(path), copy, StringComparison.OrdinalIgnoreCase))
                File.Copy(path, copy, overwrite: true);

            Edit(e => EpisodeEditor.AttachVideo(e, clip.Id, copy, fit));
            DraftDialogue = fit.Dialogue;
            FitMessage = fit.WithinTolerance
                ? $"Video {length:m\\:ss} · narration fitted to {fit.Duration.TotalSeconds:0.0} s"
                : $"Closest fit is {Math.Abs(fit.Gap.TotalSeconds):0.0} s too {(fit.Gap > TimeSpan.Zero ? "short" : "long")}. Edit the dialogue to close the gap.";
        }
        catch (OperationCanceledException)
        {
            FitMessage = null;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or FfmpegException or IOException or InvalidDataException or InvalidOperationException)
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

    [ObservableProperty]
    public partial double RenderProgress { get; set; }

    private bool CanRender => HasEpisode && FfmpegAvailable;

    [RelayCommand(CanExecute = nameof(CanRender), IncludeCancelCommand = true)]
    private async Task BuildPreviewAsync(CancellationToken ct)
    {
        await RunRenderAsync("Preview ready.", async progress =>
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
        await RunRenderAsync(null, async progress =>
            output = await Task.Run(() => _builder.ExportAsync(Episode!, EpisodeFolder!, preset, Captions, progress, ct), ct));
        if (output is not null)
            StatusMessage = $"Saved to {output}";
    }

    private async Task RunRenderAsync(string? doneMessage, Func<IProgress<double>, Task> work)
    {
        ErrorMessage = null;
        StatusMessage = null;
        RenderProgress = 0;
        try
        {
            await work(new Progress<double>(p => RenderProgress = p * 100)); // Progress<T> hops back to the UI thread
            StatusMessage = doneMessage;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception e) when (e is FfmpegException or IOException)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            RenderProgress = 0;
        }
    }
}
