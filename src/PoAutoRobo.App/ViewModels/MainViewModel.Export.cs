using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.UI;

namespace PoAutoRobo.App.ViewModels;

// The last two steps: how the captions look, the music and the quick preview; then the finished videos.
public partial class MainViewModel
{
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

    private void SetCaptions(CaptionStyle style) => Edit(e => e with { Captions = style });

    /// <summary>The captions' size or outline is not the standard one. Their controls are advanced, and show too while this is so.</summary>
    public bool CaptionsTuned => Captions.FontSize != Standard.FontSize || Captions.StrokeWidth != Standard.StrokeWidth;

    private static readonly CaptionStyle Standard = new();

    // ---- Music ----

    public static IReadOnlyList<string> MusicExtensions { get; } = [".mp3", ".wav", ".m4a", ".flac", ".ogg"];

    public bool HasMusic => Episode?.MusicPath is not null;

    /// <summary>What the outro music button says: the track in use, or that the app makes its own.</summary>
    public string MusicLabel => Episode?.MusicPath is { } track ? $"Music · {Path.GetFileName(track)}" : "The app's own tune · change…";

    /// <summary>Copies a track into the episode and plays it over the outro from the next preview or render on.</summary>
    public Task SetMusicAsync(string path) => Guard(async () =>
    {
        if (Episode is null || EpisodeFolder is not { } folder) return;
        if (!MusicExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Music must be an MP3, WAV, M4A, FLAC or OGG file.");
        var copy = Path.Combine(folder, "imports", "music-" + Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        if (!string.Equals(Path.GetFullPath(path), copy, StringComparison.OrdinalIgnoreCase))
            await Task.Run(() => File.Copy(path, copy, overwrite: true));
        Edit(e => EpisodeEditor.SetMusic(e, copy));
        StatusMessage = "Music added. It plays over the outro at the end, from the next preview or render.";
    });

    [RelayCommand(CanExecute = nameof(HasMusic))]
    private void RemoveMusic() => Edit(e => EpisodeEditor.SetMusic(e, null));

    // ---- Preview and export ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial Preview? Preview { get; set; }

    public bool HasPreview => Preview is not null;

    /// <summary>The video or folder of videos most recently rendered for this episode, for the "Show in folder" button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExport))]
    [NotifyCanExecuteChangedFor(nameof(ShowExportCommand))]
    public partial string? LastExport { get; set; }

    public bool HasExport => LastExport is not null;

    [RelayCommand(CanExecute = nameof(HasExport))]
    private void ShowExport() => ShowInExplorer(LastExport!);

    // Not while a clip or teaser is being got ready to watch either: it draws into the same store of finished clips.
    private bool CanRender => HasEpisode && FfmpegAvailable && !IsWorking && !IsWatching;

    [RelayCommand(CanExecute = nameof(CanRender))]
    private Task BuildPreviewAsync() => RunActivityAsync("Building the preview", BuildPreviewNowAsync, "Preview ready.");

    /// <summary>Builds the quick preview for the player on the Preview step. Part of a job already under way.</summary>
    private async Task BuildPreviewNowAsync(CancellationToken ct)
    {
        ReleasePreview();
        Preview = null;
        var (episode, folder, progress) = (Episode!, EpisodeFolder!, RenderProgress());
        Preview = await Task.Run(() => _builder.PreviewAsync(episode, folder, SeriesBrand, progress, ct), ct);
        SyncCards(); // every clip has now been recorded
    }

    [RelayCommand(CanExecute = nameof(CanRender))]
    private async Task RenderAsync()
    {
        var preset = ExportChoices[ExportPresetIndex].Value;
        if (await RunActivityAsync($"Rendering the master video ({preset.Height}p, {preset.Fps} fps)", ct => RenderMasterAsync(preset, ct)))
            Premiere();
    }

    /// <summary>Renders the master video and writes everything that is uploaded with it. Part of a job already under way.</summary>
    private async Task RenderMasterAsync(ExportPreset preset, CancellationToken ct)
    {
        var (episode, folder, captions, progress) = (Episode!, EpisodeFolder!, Captions, RenderProgress());
        var video = await Task.Run(() => _builder.ExportAsync(episode, folder, preset, captions, SeriesBrand, progress, ct), ct);
        LastExport = video;
        SyncCards();

        // The upload details are a help, not part of the video: failing to write them never fails the render.
        var withNotes = ", subtitles and thumbnail";
        try
        {
            ReportActivity("Writing the title, description and tags to upload with it", 1);
            var notes = (await _scriptWriter.WritePublishNotesAsync(episode, ct)).InSeries(_kit.ChannelName, episode.Number);
            await File.WriteAllTextAsync(Path.ChangeExtension(video, ".description.txt"), PublishPack.Description(notes), ct);
            withNotes = ", subtitles, thumbnail and upload description";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ErrorMessage = "The video is finished, but its upload description could not be written. " + Plain(e);
        }
        var bookends = _kit.ChannelName.Trim().Length > 0 ? "" : " It has no intro or outro: give the channel a name under Series to add them.";
        StatusMessage = $"Saved {Path.GetFileName(video)}, with its chapter list{withNotes} beside it.{bookends}";
    }

    // Not while one picture is being drawn, for the reason a batch of pictures may not start then.
    private bool CanMakeAll => CanRender && !GeneratePictureCommand.IsRunning && !AnotherTakeCommand.IsRunning;

    /// <summary>
    /// Everything from the script to the finished video in one job: every voice, every picture still wanted, then
    /// the master video. Asked about once, with the cost, and then left to run.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMakeAll))]
    private Task MakeAllAsync() => MakeEpisodeAsync(draft: false);

    /// <summary>
    /// The same, ending on the quick preview instead of the master: a draft to watch, to find what to change before
    /// anything is rendered at full size.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMakeAll))]
    private Task MakeDraftAsync() => MakeEpisodeAsync(draft: true);

    private async Task MakeEpisodeAsync(bool draft)
    {
        var estimate = CostEstimate.For(Episode!, _imageModel, Quality);
        var preset = ExportChoices[ExportPresetIndex].Value;
        var draw = PicturesAvailable && !estimate.NothingToDo;
        ErrorMessage = null;
        if (draw && OverBudget(estimate.Dollars ?? 0) is { } refusal)
        {
            ErrorMessage = refusal;
            return;
        }
        var pictures = draw ? $"draws {char.ToLowerInvariant(estimate.Summary[0])}{estimate.Summary[1..]}" : PicturesAvailable ? "draws nothing new, as every clip has its picture." : "draws no pictures, as the picture service is not connected.";
        var held = draw && estimate.Dollars is { } agreed ? $" It stops drawing rather than spend more than {SpendLog.Money(agreed)}." : "";
        var end = draft ? "Then builds the quick preview to watch on the Preview step; nothing is rendered at full size." : $"Then renders the master video at {preset.Height}p, {preset.Fps} fps.";
        var plan = $"Records every clip's voice, {pictures}{held} {end} You can stop it at any point and keep what is made.";
        if (!await ConfirmSpend(draft ? "Make a draft to watch?" : "Make the whole episode?", plan, "Make it", draw ? estimate.Dollars ?? 0 : 0) || !CanMakeAll)
            return;

        (List<string> Failures, string? Halted) drawn = ([], null);
        var finished = await RunActivityAsync(draft ? "Making a draft of the episode" : "Making the whole episode", async ct =>
        {
            Phase(0, 0.15);
            await NarrateEveryClipAsync(ct);
            if (draw)
            {
                Phase(0.15, draft ? 0.55 : 0.35);
                (_, drawn.Failures, drawn.Halted) = await DrawAllAsync(estimate, ct);
            }
            if (draft)
            {
                Phase(0.7, 0.3);
                await BuildPreviewNowAsync(ct);
                StatusMessage = "The draft is ready to watch. Change what needs changing, then render the master video.";
            }
            else
            {
                Phase(0.5, 0.5);
                await RenderMasterAsync(preset, ct);
            }
        });
        if (finished && draft) Step = PreviewStep; // where the draft plays; a step change clears errors, so it comes before them
        if (finished && !draft) Premiere();
        if (drawn.Halted is { } halted) StatusMessage = $"{StatusMessage} {halted}".Trim();
        ReportFailures(drawn.Failures);
    }

    // ---- The next part of the episode to make, for the bar under the header on the clips page ----

    // Voices first, as they fix every clip's length; then the pictures still wanted; then a draft to watch it all in.
    private (string Label, System.Windows.Input.ICommand Command) NextJob()
    {
        if (Clips.Any(c => c.Measured is null && !c.OwnSound)) return ("Record voices", NarrateAllCommand);
        if (Episode is { } episode && PicturesAvailable && !CostEstimate.For(episode, _imageModel, Quality).NothingToDo) return ("Generate pictures", GenerateAllCommand);
        return ("Make a draft", MakeDraftCommand);
    }

    // ---- Watching one clip or one teaser on its own ----

    /// <summary>Set by the window: plays a short video beside the card it was made for, a clip's or a teaser's.</summary>
    public Action<object, string> PlayVideo { get; set; } = (_, _) => { };

    private CancellationTokenSource? _watching;

    /// <summary>True while a clip or teaser is being got ready to watch. Renders wait meanwhile.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenderCommand), nameof(SuggestTeasersCommand), nameof(RenderTeasersCommand), nameof(BuildPreviewCommand), nameof(MakeAllCommand),
        nameof(MakeDraftCommand), nameof(WatchClipCommand), nameof(WatchTeaserCommand))]
    public partial bool IsWatching { get; set; }

    /// <summary>
    /// Plays one clip on its own, small and with its sound. Drawn as the quick preview draws it, so after a preview it
    /// plays at once. The voice is recorded first if this wording has not been, as Audition does.
    /// </summary>
    /// <param name="card">The clip; the selected one when null, as from the inspector.</param>
    [RelayCommand(CanExecute = nameof(CanRender))]
    private async Task WatchClipAsync(ClipViewModel? card)
    {
        if ((card ?? SelectedClip) is not { } clip || Episode is not { } episode) return;
        var index = episode.Clips.ToList().FindIndex(c => c.Id == clip.Id);
        if (index < 0) return; // taken out of the episode meanwhile
        clip.IsBusy = true;
        try
        {
            await WatchAsync(clip, (folder, ct) => _builder.ClipPreviewAsync(episode, folder, index, SeriesBrand, ct));
        }
        finally
        {
            clip.IsBusy = false;
        }
        SyncCards(); // its voice may have been recorded just now
    }

    /// <summary>
    /// A card's own play button, which is there whatever else is going on: plays when it can, and says why when not.
    /// </summary>
    private void Watch(object card)
    {
        IRelayCommand command = card is TeaserViewModel ? WatchTeaserCommand : WatchClipCommand;
        if (command.CanExecute(card))
            command.Execute(card);
        else if (IsWorking || IsWatching)
            StatusMessage = "Wait for the job under way to finish, then press play again.";
        else if (!FfmpegAvailable)
            ErrorMessage = "FFmpeg is needed to play a clip. Install it, then restart the app.";
    }

    /// <summary>Makes a video to watch and plays it beside its card. Stopped when the window closes, so FFmpeg goes with it.</summary>
    private async Task WatchAsync(object card, Func<string, CancellationToken, Task<string>> make)
    {
        if (EpisodeFolder is not { } folder) return;
        using var watching = _watching = new CancellationTokenSource();
        IsWatching = true;
        ErrorMessage = null;
        try
        {
            var video = await Task.Run(() => make(folder, watching.Token), watching.Token);
            if (EpisodeFolder == folder) PlayVideo(card, video); // not when another episode was opened meanwhile
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch (Exception e)
        {
            ErrorMessage = Plain(e);
        }
        finally
        {
            _watching = null;
            IsWatching = false;
        }
    }

    /// <summary>Stops a clip or teaser being got ready to watch, and a still of the intro being drawn. The window calls this as it closes.</summary>
    public void StopWatching()
    {
        _watching?.Cancel();
        _still?.Cancel();
    }

    // Progress<T> hops back to the UI thread, so it must be made on it.
    private Progress<RenderProgress> RenderProgress() => new(p =>
    {
        if (!IsWorking) return; // a late report after the job ended
        ReportActivity(p.Activity, p.Fraction);
        DrawingClips = p.Clips is not null;
        for (var i = 0; p.Clips is { } clips && i < Math.Min(clips.Count, Clips.Count); i++)
            Clips[i].Progress = clips[i] * 100;
    });
}
