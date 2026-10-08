using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Pipeline;

/// <summary>A quick low-resolution render for the preview player, plus the word timings its caption overlay draws from.</summary>
public sealed record Preview(string VideoPath, IReadOnlyList<CaptionSegment> Segments);

/// <summary>Where a render has got to: what it is doing in plain words, and how far through the whole job it is (0 to 1).</summary>
public sealed record RenderProgress(string Activity, double Fraction);

/// <summary>Turns an episode into narration files and a finished video inside the episode folder.</summary>
public sealed partial class EpisodeBuilder(INarrator narrator, FfmpegRunner ffmpeg)
{
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".webm", ".avi"];
    private static readonly ExportPreset Draft = new(640, 360, 30);

    /// <summary>Narrates every clip's active dialogue. Audio is cached by its text, so only changed clips are spoken again.</summary>
    public async Task<IReadOnlyList<Narration>> NarrateAsync(Episode episode, string folder, CancellationToken ct)
    {
        var narrations = new List<Narration>(episode.Clips.Count);
        foreach (var clip in episode.Clips)
            narrations.Add(await NarrateClipAsync(clip, folder, ct));
        return narrations;
    }

    public async Task<Narration> NarrateClipAsync(Clip clip, string folder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = clip.Active.Dialogue;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{narrator.GetType().Name}|{clip.NarrationRate:0.###}|{text}"))))[..16];
        var audio = Path.Combine(folder, "audio", $"{clip.Id:N}-{key}.wav");
        var words = Path.ChangeExtension(audio, ".words.json");

        // One narration at a time. Two requests for the same line (an edit being applied while Audition is pressed)
        // would otherwise both write the same file; the second now waits and finds the first one's result.
        await _narrationGate.WaitAsync(ct);
        try
        {
            if (File.Exists(audio) && File.Exists(words))
                return new Narration(audio, WavInfo.Duration(audio), JsonSerializer.Deserialize<List<WordTiming>>(await File.ReadAllTextAsync(words, ct))!);

            var narration = await narrator.SynthesizeAsync(text, audio, clip.NarrationRate, ct);
            await File.WriteAllTextAsync(words, JsonSerializer.Serialize(narration.Words), ct);
            return narration;
        }
        finally
        {
            _narrationGate.Release();
        }
    }

    private readonly SemaphoreSlim _narrationGate = new(1, 1);

    public Task<TimeSpan> ProbeDurationAsync(string path, CancellationToken ct) => ffmpeg.ProbeDurationAsync(path, ct);

    /// <returns>Path of the finished video in the episode's export folder.</returns>
    public async Task<string> ExportAsync(Episode episode, string folder, ExportPreset preset, CaptionStyle captions, IProgress<RenderProgress>? progress, CancellationToken ct)
    {
        var output = Path.Combine(folder, "export", $"{ProjectStore.Slug(episode.Title)}-{preset.Height}p{preset.Fps}.mp4");
        await RenderAsync(episode, folder, preset, captions, output, progress, ct);
        return output;
    }

    /// <summary>Small, fast render with no captions burned in; the preview draws them live so style changes show at once.</summary>
    public async Task<Preview> PreviewAsync(Episode episode, string folder, IProgress<RenderProgress>? progress, CancellationToken ct)
    {
        var output = Path.Combine(folder, "export", "preview.mp4");
        return new Preview(output, await RenderAsync(episode, folder, Draft, null, output, progress, ct));
    }

    private async Task<IReadOnlyList<CaptionSegment>> RenderAsync(
        Episode episode, string folder, ExportPreset preset, CaptionStyle? captions, string output, IProgress<RenderProgress>? progress, CancellationToken ct)
    {
        // The job has three stages. Their shares of the bar are rough but fixed, so it only ever moves forward.
        const double VoiceShare = 0.10, DrawShare = 0.80; // drawing is nearly all of the work now; the join only copies
        var count = episode.Clips.Count;
        var narrations = new List<Narration>(count);
        for (var i = 0; i < count; i++)
        {
            progress?.Report(new RenderProgress($"Recording the voice for clip {i + 1} of {count} · {episode.Clips[i].Title}", VoiceShare * i / count));
            narrations.Add(await NarrateClipAsync(episode.Clips[i], folder, ct));
        }
        var slots = FfmpegArgs.Timeline([.. narrations.Select(n => n.Duration)]);
        var segments = narrations.Select((n, i) => new CaptionSegment(slots[i].Start, n.Words)).ToList();
        // Scratch files live in the temp folder, never in the episode folder: that is usually inside a synced
        // Documents folder, where the sync client locks new files and refuses the cleanup.
        var work = Directory.CreateTempSubdirectory("poautorobo-render-").FullName;
        var partial = Path.Combine(work, "master.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            // Every clip is encoded once, finished (captions and fades included), several at a time. The join then only
            // copies them, so the slow work is spread across the processor's cores and nothing is encoded twice.
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeuib.ttf"), Path.Combine(work, FfmpegArgs.TitleFontFile), overwrite: true);
            var pictures = Enumerable.Range(0, count).Select(i => $"clip_{i:00}.mp4").ToList();
            var done = new double[count];      // how far each clip has got, 0 to 1
            var inHand = new SortedSet<int>();
            var gate = new object();
            void ReportDrawing()
            {
                // Called under the lock, so reports leave in order and the bar never steps backwards.
                var finished = done.Count(d => d >= 1);
                var names = inHand.Count == 0 ? "" : " · working on: " + string.Join(", ", inHand.Select(i => episode.Clips[i].Title));
                progress?.Report(new RenderProgress($"Drawing clips · {finished} of {count} done{names}", VoiceShare + DrawShare * done.Sum() / count));
            }
            lock (gate) ReportDrawing();

            var workers = Math.Clamp(Environment.ProcessorCount / 3, 1, 4);
            await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, async (i, token) =>
            {
                var clip = episode.Clips[i];
                var (source, input) = PictureFor(clip);
                if (source == ClipSource.TitleCard)
                {
                    input = $"title_{i:00}.txt"; // read by name from the working folder, which avoids filter-path escaping
                    await File.WriteAllTextAsync(Path.Combine(work, input), clip.Title, token);
                }
                string? captionFile = null;
                if (captions is not null)
                {
                    captionFile = $"captions_{i:00}.ass"; // timed from the start of this clip
                    await File.WriteAllTextAsync(Path.Combine(work, captionFile), AssCaptions.Build([new CaptionSegment(TimeSpan.Zero, narrations[i].Words)], captions), token);
                }
                var panels = PanelsFor(clip);
                var args = panels.Count > 1
                    ? FfmpegArgs.PanelsVideo(panels, slots[i].VideoLength, preset, pictures[i], captionFile)
                    : FfmpegArgs.ClipVideo(source, input, slots[i].VideoLength, preset, pictures[i], captionFile);

                lock (gate) { inHand.Add(i); ReportDrawing(); }
                await ffmpeg.RunAsync(args, work, slots[i].VideoLength, new Relay(p => { lock (gate) { done[i] = Math.Min(p, 0.99); ReportDrawing(); } }), token);
                lock (gate) { done[i] = 1; inHand.Remove(i); ReportDrawing(); }
            });

            await File.WriteAllTextAsync(Path.Combine(work, "clips.txt"), FfmpegArgs.ClipList(pictures), ct);
            var total = slots[^1].Start + slots[^1].VideoLength;
            const string Joining = "Joining the clips and levelling the sound";
            var joinProgress = progress is null ? null : new Relay(p => progress.Report(new RenderProgress(Joining, VoiceShare + DrawShare + (1 - VoiceShare - DrawShare) * p)));
            await ffmpeg.RunAsync(FfmpegArgs.Join("clips.txt", [.. narrations.Select(n => n.AudioPath)], partial), work, total, joinProgress, ct);

            File.Move(partial, output, overwrite: true);
            progress?.Report(new RenderProgress("Done", 1));
            return segments;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Leftover scratch in the temp folder is harmless; it must not turn a finished render into an error.
            }
        }
    }

    /// <summary>Uses the clip's footage or generated media when the file exists; otherwise a title card.</summary>
    private static (ClipSource Source, string Input) PictureFor(Clip clip)
    {
        var path = clip.Visual.Kind == VisualKind.TitleCard ? null : clip.Visual.UserVideoPath ?? clip.Visual.MediaPaths?.FirstOrDefault();
        if (path is null || !File.Exists(path))
            return (ClipSource.TitleCard, "");
        var isVideo = VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        return (isVideo ? ClipSource.Video : ClipSource.Image, Path.GetFullPath(path));
    }

    /// <summary>The pictures of a panel sequence that are on disk; fewer than two means it is not rendered as a sequence.</summary>
    private static List<string> PanelsFor(Clip clip) =>
        clip.Visual.Kind == VisualKind.MultiPanel
            ? [.. (clip.Visual.MediaPaths ?? []).Where(p => File.Exists(p) && !VideoExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).Select(Path.GetFullPath)]
            : [];


    /// <summary>Reports on the calling thread; <see cref="Progress{T}"/> would post to a context and reorder values.</summary>
    private sealed class Relay(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
