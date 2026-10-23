using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace PoAutoRobo.App.ViewModels;

// The owner's films: listed for the next episode, then watched and listened to so the script can be written to them.
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
}
