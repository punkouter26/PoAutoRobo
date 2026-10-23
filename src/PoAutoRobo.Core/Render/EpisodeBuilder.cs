namespace PoAutoRobo.Core.Render;

/// <summary>The series brand kit and the folder holding its files, for the intro and outro.</summary>
public sealed record Brand(SeriesKit Kit, string Folder)
{
    /// <summary>The channel's name as the intro and outro show it; a stand-in until one is set.</summary>
    public string ChannelName => Kit.ChannelName.Trim() is { Length: > 0 } name ? name : "Your channel";
}

/// <summary>Turns an episode into narration files and a finished video inside the episode folder.</summary>
public sealed partial class EpisodeBuilder(INarrator narrator, FfmpegRunner ffmpeg)
{
    public static readonly IReadOnlyList<string> VideoExtensions = [".mp4", ".mov", ".mkv", ".webm", ".avi"];
    private static readonly ExportPreset Draft = new(640, 360, 30);
    private static readonly TimeSpan ClipsKeptFor = TimeSpan.FromDays(14);

    /// <summary>Name of the folder of finished clips under <see cref="Files.ScratchRoot"/>; it clears itself by age.</summary>
    public const string ClipCacheName = "clips";

    /// <summary>
    /// Finished clip pictures, kept between renders. A clip whose words, picture, captions and size are unchanged is
    /// not encoded again, so a render after one edit redoes one clip. In the temp folder, never the episode folder:
    /// that is usually inside a synced Documents folder, where the sync client locks new files.
    /// </summary>
    public string ClipCacheFolder { get; init; } = Path.Combine(Files.ScratchRoot, ClipCacheName);

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
            foreach (var path in clip.AllMedia())
            {
                kept.Add(Path.GetFullPath(path, folder));
                kept.Add(Path.GetFullPath(Clip.PosterFor(path), folder)); // the frame that stands for footage on its card
                kept.Add(Path.GetFullPath(Clip.HeardFor(path), folder)); // what is said in the owner's film
            }
        }
        if (episode.MusicPath is { } music)
            kept.Add(Path.GetFullPath(music, folder));
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
        // From the middle: past any fade up from black, and for an animation after it has begun to build.
        var length = await ffmpeg.ProbeDurationAsync(videoPath, ct);
        await ffmpeg.RunAsync(FfmpegArgs.Still(videoPath, length / 2, 640, poster), Path.GetDirectoryName(Path.GetFullPath(videoPath))!, null, null, ct);
        return poster;
    }

    /// <returns>Path of the finished video in the episode's export folder.</returns>
    /// <param name="brand">The series kit, for the intro and outro; null, or a kit with no channel name, for neither.</param>
    public async Task<string> ExportAsync(Episode episode, string folder, ExportPreset preset, CaptionStyle captions, Brand? brand, IProgress<RenderProgress>? progress, CancellationToken ct)
    {
        // Numbered, so the series' videos sort in order wherever they are put.
        var number = episode.Number > 0 ? $"{episode.Number:00}-" : "";
        var output = Path.Combine(folder, "export", $"{number}{ProjectStore.Slug(episode.Title)}-{preset.Height}p{preset.Fps}.mp4");
        var (segments, marks) = await RenderAsync(episode, folder, preset, captions, brand, output, progress, ct);

        // Beside the video, ready to upload with it: chapter list, subtitles and a cover picture. YouTube shows chapters
        // only when the first starts at 0:00 and none is under ten seconds, so the intro and outro join their neighbours'.
        var name = Path.ChangeExtension(output, null);
        var chapters = marks.Where(m => !m.Bookend).Select((m, i) => (i == 0 ? TimeSpan.Zero : m.Start, m.Title));
        await File.WriteAllTextAsync(name + ".chapters.txt", PublishPack.Chapters(chapters), ct);
        await File.WriteAllTextAsync(name + ".srt", PublishPack.Srt(segments), ct);
        if (episode.Cover() is { } cover)
            File.Copy(cover, name + ".thumbnail.png", overwrite: true);
        return output;
    }

    /// <summary>
    /// One clip on its own, small and with its sound, to watch from its card. Drawn exactly as the quick preview
    /// draws it, so after a preview every clip plays at once; before one, only the clip asked for is drawn. Records the
    /// clip's voice first when that wording has not been recorded.
    /// </summary>
    /// <returns>The video's path, in the clip cache.</returns>
    public async Task<string> ClipPreviewAsync(Episode episode, string folder, int index, Brand? brand, CancellationToken ct)
    {
        var narration = await NarrateClipAsync(episode.Clips[index], folder, ct);
        // The picture runs its slot in the preview: the crossfade into the next sound shortens every clip but the very last.
        var last = index == episode.Clips.Count - 1 && Bookends(brand) is null;
        var slot = new Slot(TimeSpan.Zero, last ? narration.Duration : narration.Duration - FfmpegArgs.AudioCrossfade);
        var work = Files.NewScratchFolder("clip");
        var cache = new MediaCache(ClipCacheFolder);
        try
        {
            CopyTitleFont(work);
            var picture = (await DrawClipsAsync(episode, _ => narration.Words, _ => slot, Draft, null, [index], work, cache, null, ct))[index]!;
            return await cache.GetOrCreateAsync(["clip with its sound", picture, narration.AudioPath], ".mp4", scratch =>
                ffmpeg.RunAsync(FfmpegArgs.Mux(picture, narration.AudioPath, scratch), work, null, null, ct));
        }
        finally
        {
            DeleteScratch(work);
        }
    }

    /// <summary>The kit when it names a channel, which is what an intro, an outro or a channel bar needs; null otherwise.</summary>
    private static Brand? Bookends(Brand? brand) => brand is { Kit.ChannelName: var channel } && channel.Trim().Length > 0 ? brand : null;

    private static void DeleteScratch(string work)
    {
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Leftover scratch in the temp folder is harmless, and swept at the next launch; it must not turn finished work into an error.
        }
    }

    /// <summary>Small, fast render with no captions burned in; the preview draws them live so style changes show at once.</summary>
    public async Task<Preview> PreviewAsync(Episode episode, string folder, Brand? brand, IProgress<RenderProgress>? progress, CancellationToken ct)
    {
        var output = Path.Combine(folder, "export", "preview.mp4");
        var (segments, marks) = await RenderAsync(episode, folder, Draft, null, brand, output, progress, ct);
        return new Preview(output, segments, marks);
    }

    private async Task<(IReadOnlyList<CaptionSegment> Segments, IReadOnlyList<ClipMark> Marks)> RenderAsync(
        Episode episode, string folder, ExportPreset preset, CaptionStyle? captions, Brand? brand, string output, IProgress<RenderProgress>? progress, CancellationToken ct)
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
        // Scratch files live in the temp folder too, for the same reason the finished clips do.
        var work = Files.NewScratchFolder("render");
        var partial = Path.Combine(work, "master.mp4");
        Files.EnsureFolderFor(output);
        ForgetOldClips();
        var cache = new MediaCache(ClipCacheFolder);
        try
        {
            // The series intro and outro are slots of their own at the two ends, each with its own sound, so the
            // timeline, the join and the levelling treat them like clips. A kit that names no channel has no bookends.
            var ends = Bookends(brand);
            var first = ends is null ? 0 : 1;
            List<Narration> sounds = ends is null ? [.. narrations]
                : [await BookendSoundAsync(true, ends, null, work, cache, ct), .. narrations, await BookendSoundAsync(false, ends, episode.MusicPath, work, cache, ct)];
            var slots = FfmpegArgs.Timeline([.. sounds.Select(n => n.Duration)]);
            var segments = narrations.Select((n, i) => new CaptionSegment(slots[i + first].Start, n.Words)).ToList();

            CopyTitleFont(work);
            var pictures = await DrawClipsAsync(episode, i => narrations[i].Words, i => slots[i + first], preset, captions, [.. Enumerable.Range(0, count)], work, cache,
                (activity, fraction, clips) => progress?.Report(new RenderProgress(activity, VoiceShare + DrawShare * fraction, clips)), ct);

            // The bookends' pictures run exactly their slots, which the crossfade into the next clip shortens a little.
            List<string> joined = ends is null ? [.. pictures.OfType<string>()]
                : [await BookendPictureAsync(true, ends, episode, preset, slots[0].VideoLength, work, cache, ct), .. pictures.OfType<string>(),
                    await BookendPictureAsync(false, ends, episode, preset, slots[^1].VideoLength, work, cache, ct)];
            await File.WriteAllTextAsync(Path.Combine(work, "clips.txt"), FfmpegArgs.ClipList(joined), ct);
            var total = slots[^1].Start + slots[^1].VideoLength;
            const string Joining = "Joining the clips and levelling the sound";
            var joinProgress = progress is null ? null : new Relay<double>(p => progress.Report(new RenderProgress(Joining, VoiceShare + DrawShare + (1 - VoiceShare - DrawShare) * p)));
            // A rush of air carries the viewer into each new beat, loudest on the cut. The first beat has the intro's sting instead.
            List<TimeSpan> cuts = [.. Enumerable.Range(1, Math.Max(0, count - 1))
                .Where(i => episode.Clips[i].Beat is { } beat && beat != episode.Clips[i - 1].Beat)
                .Select(i => slots[i + first].Start)];
            if (cuts.Count > 0)
                await File.WriteAllBytesAsync(Path.Combine(work, "whoosh.wav"), Synth.Whoosh(), ct);
            await ffmpeg.RunAsync(FfmpegArgs.Join("clips.txt", [.. sounds.Select(n => n.AudioPath)], partial, cuts.Count > 0 ? "whoosh.wav" : null, cuts), work, total, joinProgress, ct);

            File.Move(partial, output, overwrite: true);
            progress?.Report(new RenderProgress("Done", 1));
            var marks = narrations.Select((n, i) => new ClipMark(episode.Clips[i].Title, slots[i + first].Start, n.AudioPath)).ToList();
            if (ends is not null)
            {
                marks.Insert(0, new ClipMark("Intro", TimeSpan.Zero, sounds[0].AudioPath, Bookend: true));
                marks.Add(new ClipMark("Outro", slots[^1].Start, sounds[^1].AudioPath, Bookend: true));
            }
            return (segments, marks);
        }
        finally
        {
            DeleteScratch(work);
        }
    }

    /// <summary>
    /// Encodes the finished picture of each clip asked for (captions and fades included), several at a time, through
    /// the clip cache. A join then only copies them, so the slow work is spread across the processor's cores and
    /// nothing is encoded twice.
    /// </summary>
    /// <param name="wordsOf">The timed words of each clip's sound, by its place in the episode, for its captions.</param>
    /// <param name="slotOf">The slot each clip fills on the timeline, by its place in the episode.</param>
    /// <param name="which">The places of the clips to draw; the others are left null.</param>
    /// <param name="report">Told how the drawing is going: in words, how far through it is (0 to 1), and how far each clip has got.</param>
    private async Task<string?[]> DrawClipsAsync(Episode episode, Func<int, IReadOnlyList<WordTiming>> wordsOf, Func<int, Slot> slotOf, ExportPreset preset, CaptionStyle? captions,
        IReadOnlyList<int> which, string work, MediaCache cache, Action<string, double, double[]>? report, CancellationToken ct)
    {
        var count = episode.Clips.Count;
        var pictures = new string?[count];
        var done = new double[count];      // how far each clip has got, 0 to 1
        var inHand = new SortedSet<int>();
        var gate = new object();
        void ReportDrawing()
        {
            // Called under the lock, so reports leave in order and the bar never steps backwards.
            var finished = done.Count(d => d >= 1);
            var names = inHand.Count == 0 ? "" : " · working on: " + string.Join(", ", inHand.Select(i => episode.Clips[i].Title));
            report?.Invoke($"Drawing clips · {finished} of {which.Count} done{names}", done.Sum() / Math.Max(1, which.Count), [.. done]);
        }
        lock (gate) ReportDrawing();

        var workers = Math.Clamp(Environment.ProcessorCount / 3, 1, 4);
        await Parallel.ForEachAsync(which, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, async (i, token) =>
        {
            var (clip, slot) = (episode.Clips[i], slotOf(i));
            var (source, input) = PictureFor(clip);
            var panels = PanelsFor(clip);
            // Whatever decides how the clip looks, besides the command line itself: the text and the files it reads.
            var made = new List<string>(panels.Count > 1 ? panels.Select(Stamp) : source == ClipSource.TitleCard ? [clip.Title] : [Stamp(input)]);
            if (source == ClipSource.TitleCard)
            {
                input = $"title_{i:00}.txt"; // read by name from the working folder, which avoids filter-path escaping
                await File.WriteAllTextAsync(Path.Combine(work, input), clip.Title, token);
            }
            // The owner's film through the robot's eyes, with the beat and the clip's title in its readout.
            RobotEye? eye = null;
            if (clip.RobotView && clip.Visual.UserVideoPath is not null && source == ClipSource.Video)
            {
                var label = clip.Beat is { } named ? $"{named.ToUpperInvariant()} · {clip.Title}" : clip.Title;
                eye = new RobotEye($"eye_{i:00}.txt", Fault: clip.Beat == "Fail");
                await File.WriteAllTextAsync(Path.Combine(work, eye.LabelFile), label, token);
                made.Add(label);
            }
            string? captionFile = null;
            if (captions is not null)
            {
                captionFile = $"captions_{i:00}.ass"; // timed from the start of this clip
                // The first clip of each beat names it with a strap, so the viewer sees where the episode has got to.
                // ponytail: burned in with the captions, so the preview (captions drawn live) shows none; draw it there too if it is missed.
                var strap = clip.Beat is { } beat && (i == 0 || episode.Clips[i - 1].Beat != beat) ? beat : null;
                var text = AssCaptions.Build([new CaptionSegment(TimeSpan.Zero, wordsOf(i))], captions, preset.Portrait, strap);
                await File.WriteAllTextAsync(Path.Combine(work, captionFile), text, token);
                made.Add(text);
            }
            // The camera move comes from the clip itself, so a clip keeps its move when the order changes.
            IReadOnlyList<string> Args(string to) =>
                panels.Count == 2 && clip.Visual.Kind == VisualKind.Parallax ? FfmpegArgs.ParallaxVideo(panels[0], panels[1], slot.VideoLength, preset, to, captionFile)
                : panels.Count > 1 ? FfmpegArgs.PanelsVideo(panels, slot.VideoLength, preset, to, captionFile)
                : FfmpegArgs.ClipVideo(source, input, slot.VideoLength, preset, to, captionFile, clip.Id.ToByteArray()[0] % 4, eye);
            // The scratch files are named for the clip's place in the order; that is taken out so a moved clip is still found.
            var command = string.Join('\n', Args("clip.mp4")).Replace($"_{i:00}.", "_.");

            lock (gate) { inHand.Add(i); ReportDrawing(); }
            pictures[i] = await cache.GetOrCreateAsync([command, .. made], ".mp4", scratch =>
                ffmpeg.RunAsync(Args(scratch), work, slot.VideoLength, new Relay<double>(p => { lock (gate) { done[i] = Math.Min(p, 0.99); ReportDrawing(); } }), token));
            lock (gate) { done[i] = 1; inHand.Remove(i); ReportDrawing(); }
        });
        return pictures;
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
        // Generated footage is a few seconds long and repeats under the narration; an animation is made to length,
        // and the narration is fitted to the user's own footage, so those hold their last frame for any shortfall.
        return (!isVideo ? ClipSource.Image : clip.Visual.Kind == VisualKind.AiVideo ? ClipSource.Loop : ClipSource.Video, Path.GetFullPath(path));
    }

    /// <summary>The pictures of a panel sequence or a layered picture that are on disk; fewer than two means the clip is drawn from one picture.</summary>
    private static List<string> PanelsFor(Clip clip) =>
        clip.Visual.Kind is VisualKind.MultiPanel or VisualKind.Parallax
            ? [.. (clip.Visual.MediaPaths ?? []).Where(p => File.Exists(p) && !VideoExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).Select(Path.GetFullPath)]
            : [];
}
