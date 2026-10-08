using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Pipeline;

/// <summary>A quick low-resolution render for the preview player, plus the word timings its caption overlay draws from.</summary>
public sealed record Preview(string VideoPath, IReadOnlyList<CaptionSegment> Segments);

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

        if (File.Exists(audio) && File.Exists(words))
            return new Narration(audio, WavInfo.Duration(audio), JsonSerializer.Deserialize<List<WordTiming>>(await File.ReadAllTextAsync(words, ct))!);

        var narration = await narrator.SynthesizeAsync(text, audio, clip.NarrationRate, ct);
        await File.WriteAllTextAsync(words, JsonSerializer.Serialize(narration.Words), ct);
        return narration;
    }

    public Task<TimeSpan> ProbeDurationAsync(string path, CancellationToken ct) => ffmpeg.ProbeDurationAsync(path, ct);

    /// <returns>Path of the finished video in the episode's export folder.</returns>
    public async Task<string> ExportAsync(Episode episode, string folder, ExportPreset preset, CaptionStyle captions, IProgress<double>? progress, CancellationToken ct)
    {
        var output = Path.Combine(folder, "export", $"{Slug(episode.Title)}-{preset.Height}p{preset.Fps}.mp4");
        await RenderAsync(episode, folder, preset, captions, output, progress, ct);
        return output;
    }

    /// <summary>Small, fast render with no captions burned in; the preview draws them live so style changes show at once.</summary>
    public async Task<Preview> PreviewAsync(Episode episode, string folder, IProgress<double>? progress, CancellationToken ct)
    {
        var output = Path.Combine(folder, "export", "preview.mp4");
        return new Preview(output, await RenderAsync(episode, folder, Draft, null, output, progress, ct));
    }

    private async Task<IReadOnlyList<CaptionSegment>> RenderAsync(
        Episode episode, string folder, ExportPreset preset, CaptionStyle? captions, string output, IProgress<double>? progress, CancellationToken ct)
    {
        var narrations = await NarrateAsync(episode, folder, ct);
        var slots = FfmpegArgs.Timeline([.. narrations.Select(n => n.Duration)]);
        var segments = narrations.Select((n, i) => new CaptionSegment(slots[i].Start, n.Words)).ToList();
        // Scratch files live in the temp folder, never in the episode folder: that is usually inside a synced
        // Documents folder, where the sync client locks new files and refuses the cleanup.
        var work = Directory.CreateTempSubdirectory("poautorobo-render-").FullName;
        var partial = Path.Combine(work, "master.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            // Each clip is rendered to its own file first, so the final join stays a simple, fast graph.
            var count = episode.Clips.Count;
            var pictures = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var (source, input) = PictureFor(episode.Clips[i]);
                if (source == ClipSource.TitleCard)
                {
                    input = $"title_{i:00}.txt"; // read by name from the working folder, which avoids filter-path escaping
                    await File.WriteAllTextAsync(Path.Combine(work, input), episode.Clips[i].Title, ct);
                    File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeuib.ttf"), Path.Combine(work, FfmpegArgs.TitleFontFile), overwrite: true);
                }
                var index = i;
                var clipProgress = progress is null ? null : new Relay(p => progress.Report((index + p) / count / 2));
                pictures.Add(Path.Combine(work, $"clip_{i:00}.mp4"));
                var panels = PanelsFor(episode.Clips[i]);
                var args = panels.Count > 1
                    ? FfmpegArgs.PanelsVideo(panels, slots[i].VideoLength, preset, pictures[i])
                    : FfmpegArgs.ClipVideo(source, input, slots[i].VideoLength, preset, pictures[i]);
                await ffmpeg.RunAsync(args, work, slots[i].VideoLength, clipProgress, ct);
            }

            if (captions is not null)
                await File.WriteAllTextAsync(Path.Combine(work, "captions.ass"), AssCaptions.Build(segments, captions), ct);

            var total = slots[^1].Start + slots[^1].VideoLength;
            var masterProgress = progress is null ? null : new Relay(p => progress.Report(0.5 + p / 2));
            await ffmpeg.RunAsync(
                FfmpegArgs.Master(pictures, [.. narrations.Select(n => n.AudioPath)], slots, captions is null ? null : "captions.ass", preset, partial),
                work, total, masterProgress, ct);

            File.Move(partial, output, overwrite: true);
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

    public static string Slug(string title)
    {
        var slug = NonSlug().Replace(title.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "episode" : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    /// <summary>Reports on the calling thread; <see cref="Progress{T}"/> would post to a context and reorder values.</summary>
    private sealed class Relay(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
