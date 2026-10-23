using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace PoAutoRobo.App.ViewModels;

// The owner's films: listed for the next episode, watched and listened to so the script can be written to them, then
// brought into the episode, each in its clip, with its own sound or narrated.
public partial class MainViewModel
{
    /// <summary>The owner's films for the next episode. The script places each in one clip.</summary>
    public ObservableCollection<FootageRow> Footage { get; } = [];

    /// <summary>Measures each film and lists it. Films that cannot be read, or run under 2 seconds or over 2 minutes, are left out with a word why.</summary>
    public Task AddFootageAsync(IReadOnlyList<string> paths) => RunActivityAsync("Reading your footage", async ct =>
    {
        if (!FfmpegAvailable)
            throw new InvalidOperationException("FFmpeg is needed to read your footage. Install it, then restart the app.");
        var refused = new List<string>();
        foreach (var path in paths.Where(p => !Footage.Any(f => string.Equals(f.Path, p, StringComparison.OrdinalIgnoreCase))))
        {
            TimeSpan length;
            try
            {
                length = await _builder.ProbeDurationAsync(path, ct);
            }
            catch (Exception e) when (e is FfmpegException or IOException)
            {
                refused.Add($"{Path.GetFileName(path)} could not be read");
                continue;
            }
            if (length < MinFootage || length > Conformance.MaxFootage)
                refused.Add($"{Path.GetFileName(path)} runs {length:m\\:ss}; films must be 2 seconds to 2 minutes");
            else
                Footage.Add(new FootageRow(path, length));
        }
        if (refused.Count > 0) ErrorMessage = string.Join(". ", refused) + ".";
    });

    // Shorter than Conformance.MinFootage on purpose: a film too short to narrate over still comes in, keeping its own sound (see FootageRow).
    private static readonly TimeSpan MinFootage = TimeSpan.FromSeconds(2);

    [RelayCommand]
    private void RemoveFootage(FootageRow row) => Footage.Remove(row);

    // ---- Watching and listening to the owner's films ----

    /// <summary>Frames a film is watched by. Each is a few cents' worth of tokens at most, and eight show what happens in a short film.</summary>
    private const int FramesToWatch = 8;

    /// <summary>Films watched and listened to are kept here by the film as it is on disk, so neither is paid for twice.</summary>
    private static MediaCache FootageCache => new(Path.Combine(AppPaths.Local, "footage"));

    private static string FilmKey(string path) =>
        FormattableString.Invariant($"{Path.GetFullPath(path)}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path).Ticks}");

    /// <summary>
    /// What a model saw happen in a film, from a few of its frames; null when nothing here can see them (on the
    /// stand-ins, or without FFmpeg). Kept by the film, so asking again is free.
    /// </summary>
    private async Task<FootageSeen?> WatchAsync(string film, string note, CancellationToken ct)
    {
        if (OfflineMessage is not null || !FfmpegAvailable) return null;
        var saved = await FootageCache.GetOrCreateAsync(["watched", FilmKey(film)], ".json", async path =>
        {
            var frames = await _builder.FramesAsync(film, FramesToWatch, ct);
            try
            {
                JsonFile.Save(path, await _scriptWriter.WatchAsync(frames, note, ct) ?? throw new InvalidOperationException("Nothing here can look at pictures."));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(frames[0].Path)!, recursive: true);
            }
        });
        return JsonFile.Load<FootageSeen?>(saved, null);
    }

    /// <summary>
    /// A film watched, and listened to when it keeps its own sound, for the script to be written to. A film that
    /// cannot be is written to from the owner's note alone, as before, and the reason is kept in the messages.
    /// </summary>
    private async Task<FootageNote> UnderstandAsync(FootageNote note, CancellationToken ct)
    {
        try
        {
            note = note with { Seen = await WatchAsync(note.Path, note.Note, ct) };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Remember($"“{Path.GetFileName(note.Path)}” could not be watched, so the script follows your note on it. {Plain(e)}");
        }
        if (note.OwnSound && _hear is { } hear && OfflineMessage is null && FfmpegAvailable)
            try
            {
                var saved = await FootageCache.GetOrCreateAsync(["heard", FilmKey(note.Path)], ".json",
                    async path => JsonFile.Save(path, await _builder.HearAsync(note.Path, hear, ct)));
                note = note with { Heard = JsonFile.Load<List<WordTiming>?>(saved, null) };
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Remember($"What is said in “{Path.GetFileName(note.Path)}” could not be heard, so its captions are yours to type. {Plain(e)}");
            }
        return note;
    }

    private bool CanSuggestNote => IsConnected && !IsWorking;

    /// <summary>Fills in a film's note from what a model sees happen in it. A few cents at most, and only when asked.</summary>
    [RelayCommand(CanExecute = nameof(CanSuggestNote))]
    private Task SuggestNoteAsync(FootageRow row) => RunActivityAsync("Watching your film", async ct =>
    {
        if (OfflineMessage is not null || !FfmpegAvailable)
            throw new InvalidOperationException(OfflineMessage is not null ? "Watching a film needs the live services. Sign in with az login and restart." : "FFmpeg is needed to watch a film. Install it, then restart the app.");
        var seen = await WatchAsync(row.Path, row.Note, ct) ?? throw new InvalidOperationException("The film could not be watched.");
        row.Note = seen.Note.Trim();
        StatusMessage = $"Seen in “{row.Name}”: {seen.Timeline.ReplaceLineEndings(" ")}";
    });

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

    // ---- The user's own footage ----

    [RelayCommand]
    private Task AttachVideoAsync(string path)
    {
        if (Episode is null || SelectedClip is not { } card) return Task.CompletedTask;
        if (IsWorking || !FfmpegAvailable)
        {
            ErrorMessage = null; // said again after being closed, it must show again
            ErrorMessage = IsWorking
                ? "Wait for the job in progress to finish, or cancel it, before adding your video."
                : "FFmpeg is needed to read your video. Install it, then restart the app.";
            return Task.CompletedTask;
        }
        return RunActivityAsync("Fitting the narration to your video", async ct =>
        {
            var clip = card.Clip;
            var length = await _builder.ProbeDurationAsync(path, ct);
            if (length < Conformance.MinFootage || length > Conformance.MaxFootage)
                throw new InvalidOperationException("Footage must be between 5 seconds and 2 minutes long.");
            var (copy, fit) = await ImportFootageAsync(clip, path, length, EpisodeFolder!, ct);
            Edit(e => EpisodeEditor.AttachVideo(e, clip.Id, copy, fit));
            StatusMessage = fit.WithinTolerance
                ? $"Video {length:m\\:ss} · narration fitted to {fit.Duration.TotalSeconds:0.0} s."
                : $"Closest fit is {Math.Abs(fit.Gap.TotalSeconds):0.0} s too {(fit.Gap > TimeSpan.Zero ? "short" : "long")}. Edit the dialogue to close the gap.";
        });
    }

    /// <summary>
    /// Copies one of the owner's films into the episode and, unless it keeps its own sound, fits the clip's
    /// narration to it. Gives the copy and the dialogue and pace to attach with it.
    /// </summary>
    private async Task<(string Copy, FitResult Fit)> ImportFootageAsync(Clip clip, string path, TimeSpan length, string folder, CancellationToken ct)
    {
        var fit = new FitResult(clip.Active.Dialogue, 1.0, length, TimeSpan.Zero, WithinTolerance: true); // its own sound: nothing to fit
        if (!clip.OwnSound)
        {
            ReportActivity($"Fitting the narration to “{clip.Title}”", 0.2);
            fit = await Conformance.FitAsync(clip.Active.Dialogue, length, _scriptWriter,
                async (text, rate, c) => (await _builder.NarrateClipAsync(EpisodeEditor.WithDialogue(clip, text) with { NarrationRate = rate }, folder, c)).Duration,
                ct);
        }
        // Named for the clip as well as the file, so two different videos called "take.mp4" cannot overwrite each other.
        ReportActivity("Copying your video into the episode", 0.9);
        var copy = await ImportAsync(path, folder, $"{clip.Id:N}-{Path.GetFileName(path)}", ct);
        await _builder.PosterAsync(copy, ct); // a frame of the footage, so its card is not left blank
        return (copy, fit);
    }

    /// <summary>Copies a file into the episode's imports folder under <paramref name="name"/>, and gives the copy's path.</summary>
    private static async Task<string> ImportAsync(string path, string folder, string name, CancellationToken ct)
    {
        // Already in the episode (a film being narrated again, say): used where it is, never copied a second time.
        var imports = Path.GetFullPath(Path.Combine(folder, "imports"));
        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), imports, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(path);
        var copy = Path.Combine(folder, "imports", name);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        if (!string.Equals(Path.GetFullPath(path), copy, StringComparison.OrdinalIgnoreCase))
            await Task.Run(() => File.Copy(path, copy, overwrite: true), ct); // can be hundreds of megabytes
        return copy;
    }

    /// <summary>
    /// Brings the owner's films into a newly written episode: each is copied in beside the clip the script gave it,
    /// and narrated ones have their words fitted to the film. A film too short to narrate over keeps its own sound.
    /// </summary>
    private async Task<Episode> BringInFootageAsync(Episode episode, IReadOnlyList<FootageNote> footage, string folder, CancellationToken ct)
    {
        foreach (var note in footage)
        {
            if (episode.Clips.FirstOrDefault(c => string.Equals(c.Visual.UserVideoPath, note.Path, StringComparison.OrdinalIgnoreCase)) is not { } clip)
                continue; // the script always places every film; nothing to bring in if it somehow did not
            if (!clip.OwnSound && note.Length < Conformance.MinFootage)
            {
                clip = BeatPlan.WithFootage(clip, note with { OwnSound = true });
                Note($"“{clip.Title}” keeps its own sound: under 5 seconds is too short to narrate over.");
            }
            var (copy, fit) = await ImportFootageAsync(clip, note.Path, note.Length, folder, ct);
            episode = EpisodeEditor.AttachVideo(EpisodeEditor.ReplaceClip(episode, clip), clip.Id, copy, fit);
            // What was heard said in it is kept beside the copy and becomes its captions, timed as it was said, for the owner to correct.
            if (note.Heard is { Count: > 0 } heard)
            {
                JsonFile.Save(Clip.HeardFor(copy), heard);
                if (clip.OwnSound && clip.Active.Dialogue.Trim().Length == 0)
                    episode = EpisodeEditor.ReplaceClip(episode, EpisodeEditor.WithDialogue(episode.Clips.First(c => c.Id == clip.Id), string.Join(' ', heard.Select(w => w.Text))));
            }
            if (clip.OwnSound) // its sound is taken now, which is free, so the cards and running time show the real length
                await _builder.NarrateClipAsync(episode.Clips.First(c => c.Id == clip.Id), folder, ct);
        }
        return episode;
    }

    /// <summary>
    /// Plays the selected clip's film with its own sound, or narrates over it again. Its own sound is taken at once
    /// and costs nothing; narrating again rewrites the words to the film's length and records them.
    /// </summary>
    [RelayCommand]
    private Task SetOwnSoundAsync(bool ownSound)
    {
        if (Episode is null || EpisodeFolder is not { } folder || SelectedClip is not { HasUserVideo: true } card || card.OwnSound == ownSound)
            return Task.CompletedTask;
        if (ownSound)
            return Guard(async () =>
            {
                Edit(e => EpisodeEditor.SetOwnSound(e, card.Id, true));
                await _builder.NarrateClipAsync(card.Clip, folder, CancellationToken.None); // measured, so the running time is right
                SyncCards();
            });
        return RunActivityAsync("Fitting the narration to your video", async ct =>
        {
            var film = card.Clip.Visual.UserVideoPath!;
            var length = await _builder.ProbeDurationAsync(film, ct);
            if (length < Conformance.MinFootage)
                throw new InvalidOperationException("This film is under 5 seconds, too short to narrate over, so it keeps its own sound.");
            // Words to fit from: the captions typed for it, else what the film shows, else its title. Fitting
            // scales the words it starts with, so it must start with some.
            var clip = card.Clip with { OwnSound = false };
            var seed = new[] { clip.Active.Dialogue, clip.Active.VisualPrompt, clip.Title }.FirstOrDefault(t => t.Trim().Length > 0) ?? "Your footage";
            clip = EpisodeEditor.WithDialogue(clip, seed);
            var (copy, fit) = await ImportFootageAsync(clip, film, length, folder, ct);
            Edit(e => EpisodeEditor.AttachVideo(EpisodeEditor.SetOwnSound(e, card.Id, false), card.Id, copy, fit));
        });
    }

    /// <summary>Shows the selected clip's film through the robot's eyes, or as it was filmed. Free: it is drawn here.</summary>
    [RelayCommand]
    private void SetRobotView(bool on)
    {
        if (SelectedClip is { HasUserVideo: true } card && card.RobotView != on)
            Edit(e => EpisodeEditor.SetRobotView(e, card.Id, on));
    }

    [RelayCommand]
    private void RemoveVideo()
    {
        if (SelectedClip is { } card)
            Edit(e => EpisodeEditor.RemoveVideo(e, card.Id));
    }
}
