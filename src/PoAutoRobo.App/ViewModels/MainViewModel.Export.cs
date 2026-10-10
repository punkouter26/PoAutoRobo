using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.UI;

namespace PoAutoRobo.App.ViewModels;

// The last step: how the captions look, the music, the quick preview, and the finished videos.
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

    // ---- Music ----

    public static IReadOnlyList<string> MusicExtensions { get; } = [".mp3", ".wav", ".m4a", ".flac", ".ogg"];

    public bool HasMusic => Episode?.MusicPath is not null;

    /// <summary>What the music button says: the track in use, or that there is none.</summary>
    public string MusicLabel => Episode?.MusicPath is { } track ? $"Music · {Path.GetFileName(track)}" : "Add music…";

    /// <summary>Copies a track into the episode and plays it under the narration from the next preview or render on.</summary>
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
        StatusMessage = "Music added. It plays quietly under the voice, and dips while the voice speaks, from the next preview or render.";
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

    private bool CanRender => HasEpisode && FfmpegAvailable && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanRender))]
    private Task BuildPreviewAsync() => RunActivityAsync("Building the preview", async ct =>
    {
        ReleasePreview();
        Preview = null;
        var (episode, folder, progress) = (Episode!, EpisodeFolder!, RenderProgress());
        Preview = await Task.Run(() => _builder.PreviewAsync(episode, folder, progress, ct), ct);
        SyncCards(); // every clip has now been recorded
    }, "Preview ready.");

    [RelayCommand(CanExecute = nameof(CanRender))]
    private Task RenderAsync()
    {
        var preset = ExportChoices[ExportPresetIndex].Value;
        return RunActivityAsync($"Rendering the master video ({preset.Height}p, {preset.Fps} fps)", ct => RenderMasterAsync(preset, ct));
    }

    /// <summary>Renders the master video and writes everything that is uploaded with it. Part of a job already under way.</summary>
    private async Task RenderMasterAsync(ExportPreset preset, CancellationToken ct)
    {
        var (episode, folder, captions, progress) = (Episode!, EpisodeFolder!, Captions, RenderProgress());
        var video = await Task.Run(() => _builder.ExportAsync(episode, folder, preset, captions, progress, ct), ct);
        LastExport = video;
        SyncCards();

        // The upload details are a help, not part of the video: failing to write them never fails the render.
        var withNotes = ", subtitles and thumbnail";
        try
        {
            ReportActivity("Writing the title, description and tags to upload with it", 1);
            var notes = await _scriptWriter.WritePublishNotesAsync(episode, ct);
            await File.WriteAllTextAsync(Path.ChangeExtension(video, ".description.txt"), PublishPack.Description(notes), ct);
            withNotes = ", subtitles, thumbnail and upload description";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ErrorMessage = "The video is finished, but its upload description could not be written. " + Plain(e);
        }
        StatusMessage = $"Saved {Path.GetFileName(video)}, with its chapter list{withNotes} beside it.";
    }

    /// <summary>One upright video per clip, for phone-first video sites. Each clip already stands on its own.</summary>
    [RelayCommand(CanExecute = nameof(CanRender))]
    private Task RenderShortsAsync() => RunActivityAsync("Rendering one upright short per clip", async ct =>
    {
        var (episode, folder, captions, progress) = (Episode!, EpisodeFolder!, Captions, RenderProgress());
        LastExport = await Task.Run(() => _builder.ExportShortsAsync(episode, folder, captions, progress, ct), ct);
        SyncCards();
        StatusMessage = episode.Clips.Count == 1 ? "Saved 1 short." : $"Saved {episode.Clips.Count} shorts, one for each clip.";
    });

    // Not while one picture is being drawn, for the reason a batch of pictures may not start then.
    private bool CanMakeAll => CanRender && !GeneratePictureCommand.IsRunning && !AnotherTakeCommand.IsRunning;

    /// <summary>
    /// Everything from the script to the finished video in one job: every voice, every picture still wanted, then
    /// the master video. Asked about once, with the cost, and then left to run.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMakeAll))]
    private async Task MakeAllAsync()
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
        var plan = $"Records every clip's voice, {pictures} Then renders the master video at {preset.Height}p, {preset.Fps} fps. You can stop it at any point and keep what is made.";
        if (!await Confirm("Make the whole episode?", plan, "Make it") || !CanMakeAll)
            return;

        List<string> failures = [];
        await RunActivityAsync("Making the whole episode", async ct =>
        {
            Phase(0, 0.15);
            await NarrateEveryClipAsync(ct);
            if (draw)
            {
                Phase(0.15, 0.35);
                failures = (await DrawAllAsync(estimate, ct)).Failures;
            }
            Phase(0.5, 0.5);
            await RenderMasterAsync(preset, ct);
        });
        ReportFailures(failures);
    }

    // Progress<T> hops back to the UI thread, so it must be made on it.
    private Progress<RenderProgress> RenderProgress() => new(p =>
    {
        if (!IsWorking) return; // a late report after the job ended
        ReportActivity(p.Activity, p.Fraction);
        for (var i = 0; p.Clips is { } clips && i < Math.Min(clips.Count, Clips.Count); i++)
            Clips[i].Progress = clips[i] * 100;
    });
}
