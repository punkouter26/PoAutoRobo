using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Pipeline;

/// <summary>Turns an episode into narration files and a finished video inside the episode folder.</summary>
public sealed partial class EpisodeBuilder(INarrator narrator, FfmpegRunner ffmpeg)
{
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".webm", ".avi"];

    /// <summary>Narrates every clip's active dialogue. Audio is cached by its text, so only changed clips are spoken again.</summary>
    public async Task<IReadOnlyList<Narration>> NarrateAsync(Episode episode, string folder, CancellationToken ct)
    {
        var narrations = new List<Narration>(episode.Clips.Count);
        foreach (var clip in episode.Clips)
        {
            ct.ThrowIfCancellationRequested();
            var text = clip.Active.Dialogue;
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{narrator.GetType().Name}|{text}")))[..16];
            var audio = Path.Combine(folder, "audio", $"{clip.Id:N}-{key}.wav");
            var words = Path.ChangeExtension(audio, ".words.json");

            if (File.Exists(audio) && File.Exists(words))
            {
                narrations.Add(new Narration(audio, WavInfo.Duration(audio), JsonSerializer.Deserialize<List<WordTiming>>(await File.ReadAllTextAsync(words, ct))!));
                continue;
            }
            var narration = await narrator.SynthesizeAsync(text, audio, 1.0, ct);
            await File.WriteAllTextAsync(words, JsonSerializer.Serialize(narration.Words), ct);
            narrations.Add(narration);
        }
        return narrations;
    }

    /// <returns>Path of the finished video in the episode's export folder.</returns>
    public async Task<string> ExportAsync(Episode episode, string folder, ExportPreset preset, CaptionStyle captions, IProgress<double>? progress, CancellationToken ct)
    {
        var narrations = await NarrateAsync(episode, folder, ct);
        var slots = FfmpegArgs.Timeline([.. narrations.Select(n => n.Duration)]);
        var exportFolder = Path.Combine(folder, "export");
        var work = Path.Combine(exportFolder, "work");
        var output = Path.Combine(exportFolder, $"{Slug(episode.Title)}-{preset.Height}p{preset.Fps}.mp4");
        var partial = Path.ChangeExtension(output, ".partial.mp4");
        Directory.CreateDirectory(work);
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
                await ffmpeg.RunAsync(FfmpegArgs.ClipVideo(source, input, slots[i].VideoLength, preset, pictures[i]), work, slots[i].VideoLength, clipProgress, ct);
            }

            var segments = narrations.Select((n, i) => new CaptionSegment(slots[i].Start, n.Words));
            await File.WriteAllTextAsync(Path.Combine(work, "captions.ass"), AssCaptions.Build(segments, captions), ct);

            var total = slots[^1].Start + slots[^1].VideoLength;
            var masterProgress = progress is null ? null : new Relay(p => progress.Report(0.5 + p / 2));
            await ffmpeg.RunAsync(
                FfmpegArgs.Master(pictures, [.. narrations.Select(n => n.AudioPath)], slots, "captions.ass", preset, partial),
                work, total, masterProgress, ct);

            File.Move(partial, output, overwrite: true);
            return output;
        }
        finally
        {
            File.Delete(partial);
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>Uses the clip's footage or generated media when the file exists; otherwise a title card.</summary>
    private static (ClipSource Source, string Input) PictureFor(Clip clip)
    {
        var path = clip.Visual.UserVideoPath ?? clip.Visual.MediaPaths?.FirstOrDefault();
        if (path is null || !File.Exists(path))
            return (ClipSource.TitleCard, "");
        var isVideo = VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        return (isVideo ? ClipSource.Video : ClipSource.Image, Path.GetFullPath(path));
    }

    private static string Slug(string title)
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
