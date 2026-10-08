using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.UI;

namespace PoAutoRobo.App.ViewModels;

// The last step: how the captions look, the quick preview, and the finished videos.
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

    // ---- Preview and export ----

    [ObservableProperty]
    public partial Preview? Preview { get; set; }

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
        });
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

    // Progress<T> hops back to the UI thread, so it must be made on it.
    private Progress<RenderProgress> RenderProgress() => new(p =>
    {
        if (!IsWorking) return; // a late report after the job ended
        ReportActivity(p.Activity, p.Fraction);
        for (var i = 0; p.Clips is { } clips && i < Math.Min(clips.Count, Clips.Count); i++)
            Clips[i].Progress = clips[i] * 100;
    });
}
