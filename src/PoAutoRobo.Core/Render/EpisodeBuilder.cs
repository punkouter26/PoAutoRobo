using System.Text.Json;

namespace PoAutoRobo.Core.Render;

/// <summary>A quick low-resolution render for the preview player, plus the word timings its caption overlay draws from.</summary>
/// <param name="Marks">Where each clip starts, with its narration file, for the waveform under the player.</param>
public sealed record Preview(string VideoPath, IReadOnlyList<CaptionSegment> Segments, IReadOnlyList<ClipMark> Marks);

/// <summary>One clip's place on the finished timeline.</summary>
public sealed record ClipMark(string Title, TimeSpan Start, string AudioPath);

/// <summary>Where a render has got to: what it is doing in plain words, and how far through the whole job it is (0 to 1).</summary>
/// <param name="Clips">How far each clip's picture has got (0 to 1), in running order; null outside the drawing stage.</param>
public sealed record RenderProgress(string Activity, double Fraction, IReadOnlyList<double>? Clips = null);

/// <summary>Turns an episode into narration files and a finished video inside the episode folder.</summary>
public sealed class EpisodeBuilder(INarrator narrator, FfmpegRunner ffmpeg)
{
    public static readonly IReadOnlyList<string> VideoExtensions = [".mp4", ".mov", ".mkv", ".webm", ".avi"];
    private static readonly ExportPreset Draft = new(640, 360, 30);
    private static readonly TimeSpan ClipsKeptFor = TimeSpan.FromDays(14);

    /// <summary>
    /// Finished clip pictures, kept between renders. A clip whose words, picture, captions and size are unchanged is
    /// not encoded again, so a render after one edit redoes one clip. In the temp folder, never the episode folder:
    /// that is usually inside a synced Documents folder, where the sync client locks new files.
    /// </summary>
    public string ClipCacheFolder { get; init; } = Path.Combine(Path.GetTempPath(), "poautorobo-clips");

    /// <summary>Narrates one clip's active dialogue. Audio is cached by its text, so an unchanged clip is never spoken twice.</summary>
    public async Task<Narration> NarrateClipAsync(Clip clip, string folder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = clip.Active.Dialogue;
        var audio = AudioPath(clip, text, folder);
        var words = Path.ChangeExtension(audio, ".words.json");

        // One narration at a time. Two requests for the same line (an edit being applied while Audition is pressed)
        // would otherwise both write the same file; the second now waits and finds the first one's result.
        await _narrationGate.WaitAsync(ct);
        try
        {
            if (File.Exists(audio) && File.Exists(words))
                try
                {
                    if (JsonSerializer.Deserialize<List<WordTiming>>(await File.ReadAllTextAsync(words, ct)) is { } timings)
                        return new Narration(audio, WavInfo.Duration(audio), timings);
                }
                catch (Exception e) when (e is JsonException or InvalidDataException)
                {
                    // A recording cut short (a crash, a sync conflict) is recorded again instead of failing every time.
                }

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

    private string AudioPath(Clip clip, string text, string folder)
    {
        var key = MediaCache.TextHash(FormattableString.Invariant($"{narrator.GetType().Name}|{clip.NarrationRate:0.###}|{text}"))[..16];
        return Path.Combine(folder, "audio", $"{clip.Id:N}-{key}.wav");
    }

    /// <summary>How long the clip's narration really runs, once it has been recorded; null until then.</summary>
    public TimeSpan? MeasuredDuration(Clip clip, string folder)
    {
        var audio = AudioPath(clip, clip.Active.Dialogue, folder);
        try
        {
            // The word timings are written last, so their presence means the recording is whole.
            return File.Exists(Path.ChangeExtension(audio, ".words.json")) ? WavInfo.Duration(audio) : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Narration, pictures and imported footage in the episode folder that no clip uses any more: recordings of
    /// earlier wording, replaced pictures. Narration for every depth a clip has is kept.
    /// </summary>
    public IReadOnlyList<string> UnusedMedia(Episode episode, string folder)
    {
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clip in episode.Clips)
        {
            foreach (var script in clip.Scripts.Values)
            {
                var audio = AudioPath(clip, script.Dialogue, folder);
                kept.Add(audio);
                kept.Add(Path.ChangeExtension(audio, ".words.json"));
            }
            foreach (var path in (clip.Visual.MediaPaths ?? []).Append(clip.Visual.UserVideoPath).OfType<string>())
                kept.Add(Path.GetFullPath(path, folder));
            if (clip.Visual.UserVideoPath is { } video)
                kept.Add(Path.GetFullPath(Clip.PosterFor(video), folder)); // the frame that stands for the footage on its card
        }
        return [.. new[] { "audio", "images", "imports" }
            .Select(name => Path.Combine(folder, name))
            .Where(Directory.Exists)
            .SelectMany(Directory.GetFiles)
            .Where(file => !kept.Contains(file))];
    }

    public Task<TimeSpan> ProbeDurationAsync(string path, CancellationToken ct) => ffmpeg.ProbeDurationAsync(path, ct);

    /// <summary>Saves one frame of a video beside it, to stand for the footage on its card and as the episode's thumbnail.</summary>
    /// <returns>The picture's path.</returns>
    public async Task<string> PosterAsync(string videoPath, CancellationToken ct)
    {
        var poster = Clip.PosterFor(videoPath);
        // A second in, past any fade up from black; a video shorter than that gives its first frame.
        var length = await ffmpeg.ProbeDurationAsync(videoPath, ct);
        await ffmpeg.RunAsync(["-ss", length > TimeSpan.FromSeconds(2) ? "1" : "0", "-i", videoPath, "-frames:v", "1", "-vf", "scale=640:-2", "-y", poster],
            Path.GetDirectoryName(Path.GetFullPath(videoPath))!, null, null, ct);
        return poster;
    }

    /// <returns>Path of the finished video in the episode's export folder.</returns>
    public async Task<string> ExportAsync(Episode episode, string folder, ExportPreset preset, CaptionStyle captions, IProgress<RenderProgress>? progress, CancellationToken ct)
    {
        var output = Path.Combine(folder, "export", $"{ProjectStore.Slug(episode.Title)}-{preset.Height}p{preset.Fps}.mp4");
        var (segments, marks) = await RenderAsync(episode, folder, preset, captions, output, progress, ct);

        // Beside the video, ready to upload with it: chapter list, subtitles and a cover picture.
        var name = Path.ChangeExtension(output, null);
        await File.WriteAllTextAsync(name + ".chapters.txt", PublishPack.Chapters(marks.Select(m => (m.Start, m.Title))), ct);
        await File.WriteAllTextAsync(name + ".srt", PublishPack.Srt(segments), ct);
        if (episode.Cover() is { } cover)
            File.Copy(cover, name + ".thumbnail.png", overwrite: true);
        return output;
    }

    /// <summary>One upright video per clip, captions burned in, for phone-first video sites.</summary>
    /// <returns>The folder holding them, inside the episode's export folder.</returns>
    public async Task<string> ExportShortsAsync(Episode episode, string folder, CaptionStyle captions, IProgress<RenderProgress>? progress, CancellationToken ct)
    {
        var shorts = Path.Combine(folder, "export", "shorts");
        var count = episode.Clips.Count;
        for (var i = 0; i < count; i++)
        {
            var (clip, at) = (episode.Clips[i], i);
            var part = progress is null ? null : new Relay<RenderProgress>(p => progress.Report(new RenderProgress($"Short {at + 1} of {count} · {p.Activity}", (at + p.Fraction) / count)));
            await RenderAsync(episode with { Clips = [clip] }, folder, ExportPreset.Shorts, captions, Path.Combine(shorts, $"{i + 1:00}-{ProjectStore.Slug(clip.Title)}.mp4"), part, ct);
        }
        return shorts;
    }

    /// <summary>Small, fast render with no captions burned in; the preview draws them live so style changes show at once.</summary>
    public async Task<Preview> PreviewAsync(Episode episode, string folder, IProgress<RenderProgress>? progress, CancellationToken ct)
    {
        var output = Path.Combine(folder, "export", "preview.mp4");
        var (segments, marks) = await RenderAsync(episode, folder, Draft, null, output, progress, ct);
        return new Preview(output, segments, marks);
    }

    private async Task<(IReadOnlyList<CaptionSegment> Segments, IReadOnlyList<ClipMark> Marks)> RenderAsync(
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
        // Scratch files live in the temp folder too, for the same reason the finished clips do.
        var work = Directory.CreateTempSubdirectory("poautorobo-render-").FullName;
        var partial = Path.Combine(work, "master.mp4");
        MediaCache.EnsureFolderFor(output);
        ForgetOldClips();
        var cache = new MediaCache(ClipCacheFolder);
        try
        {
            // Every clip is encoded once, finished (captions and fades included), several at a time. The join then only
            // copies them, so the slow work is spread across the processor's cores and nothing is encoded twice.
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeuib.ttf"), Path.Combine(work, FfmpegArgs.TitleFontFile), overwrite: true);
            var pictures = new string[count];
            var done = new double[count];      // how far each clip has got, 0 to 1
            var inHand = new SortedSet<int>();
            var gate = new object();
            void ReportDrawing()
            {
                // Called under the lock, so reports leave in order and the bar never steps backwards.
                var finished = done.Count(d => d >= 1);
                var names = inHand.Count == 0 ? "" : " · working on: " + string.Join(", ", inHand.Select(i => episode.Clips[i].Title));
                progress?.Report(new RenderProgress($"Drawing clips · {finished} of {count} done{names}", VoiceShare + DrawShare * done.Sum() / count, [.. done]));
            }
            lock (gate) ReportDrawing();

            var workers = Math.Clamp(Environment.ProcessorCount / 3, 1, 4);
            await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, async (i, token) =>
            {
                var clip = episode.Clips[i];
                var (source, input) = PictureFor(clip);
                var panels = PanelsFor(clip);
                // Whatever decides how the clip looks, besides the command line itself: the text and the files it reads.
                var made = new List<string>(panels.Count > 1 ? panels.Select(Stamp) : source == ClipSource.TitleCard ? [clip.Title] : [Stamp(input)]);
                if (source == ClipSource.TitleCard)
                {
                    input = $"title_{i:00}.txt"; // read by name from the working folder, which avoids filter-path escaping
                    await File.WriteAllTextAsync(Path.Combine(work, input), clip.Title, token);
                }
                string? captionFile = null;
                if (captions is not null)
                {
                    captionFile = $"captions_{i:00}.ass"; // timed from the start of this clip
                    var text = AssCaptions.Build([new CaptionSegment(TimeSpan.Zero, narrations[i].Words)], captions, preset.Portrait);
                    await File.WriteAllTextAsync(Path.Combine(work, captionFile), text, token);
                    made.Add(text);
                }
                IReadOnlyList<string> Args(string to) => panels.Count > 1
                    ? FfmpegArgs.PanelsVideo(panels, slots[i].VideoLength, preset, to, captionFile)
                    : FfmpegArgs.ClipVideo(source, input, slots[i].VideoLength, preset, to, captionFile);
                // The scratch files are named for the clip's place in the order; that is taken out so a moved clip is still found.
                var command = string.Join('\n', Args("clip.mp4")).Replace($"_{i:00}.", "_.");

                lock (gate) { inHand.Add(i); ReportDrawing(); }
                pictures[i] = await cache.GetOrCreateAsync([command, .. made], ".mp4", scratch =>
                    ffmpeg.RunAsync(Args(scratch), work, slots[i].VideoLength, new Relay<double>(p => { lock (gate) { done[i] = Math.Min(p, 0.99); ReportDrawing(); } }), token));
                lock (gate) { done[i] = 1; inHand.Remove(i); ReportDrawing(); }
            });

            await File.WriteAllTextAsync(Path.Combine(work, "clips.txt"), FfmpegArgs.ClipList(pictures), ct);
            var total = slots[^1].Start + slots[^1].VideoLength;
            const string Joining = "Joining the clips and levelling the sound";
            var joinProgress = progress is null ? null : new Relay<double>(p => progress.Report(new RenderProgress(Joining, VoiceShare + DrawShare + (1 - VoiceShare - DrawShare) * p)));
            await ffmpeg.RunAsync(FfmpegArgs.Join("clips.txt", [.. narrations.Select(n => n.AudioPath)], partial), work, total, joinProgress, ct);

            File.Move(partial, output, overwrite: true);
            progress?.Report(new RenderProgress("Done", 1));
            return (segments, [.. narrations.Select((n, i) => new ClipMark(episode.Clips[i].Title, slots[i].Start, n.AudioPath))]);
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

    /// <summary>A file's identity for the clip cache: replacing or editing it makes the clip that reads it draw again.</summary>
    private static string Stamp(string path)
    {
        var file = new FileInfo(path);
        return FormattableString.Invariant($"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
    }

    // ponytail: finished clips are dropped by age alone, so a clip unchanged for two weeks is encoded once more.
    // Track last use if that ever matters.
    private void ForgetOldClips()
    {
        try
        {
            foreach (var file in new DirectoryInfo(ClipCacheFolder).EnumerateFiles().Where(f => DateTime.UtcNow - f.LastWriteTimeUtc > ClipsKeptFor))
                file.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing kept yet, or a clip still in use by another render: either way there is nothing to do.
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
}
