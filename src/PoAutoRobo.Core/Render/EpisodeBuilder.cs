using System.Text.Json;

namespace PoAutoRobo.Core.Render;

/// <summary>The series brand kit and the folder holding its files, for the intro and outro.</summary>
public sealed record Brand(SeriesKit Kit, string Folder)
{
    /// <summary>The channel's name as the intro and outro show it; a stand-in until one is set.</summary>
    public string ChannelName => Kit.ChannelName.Trim() is { Length: > 0 } name ? name : "Your channel";
}

/// <summary>Turns an episode into narration files and a finished video inside the episode folder.</summary>
public sealed class EpisodeBuilder(INarrator narrator, FfmpegRunner ffmpeg)
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

    /// <summary>Narrates one clip's active dialogue. Audio is cached by its text, so an unchanged clip is never spoken twice.</summary>
    public async Task<Narration> NarrateClipAsync(Clip clip, string folder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = clip.Active.Dialogue;
        var audio = AudioPath(clip, text, folder);
        var words = Path.ChangeExtension(audio, ".words.json");

        // One narration at a time for each file. Two requests for the same line (an edit being applied while Audition
        // is pressed) would otherwise both write it; the second now waits and finds the first one's result. Different
        // lines are recorded side by side.
        var gate = _narrationGates.GetOrAdd(Path.GetFullPath(audio), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
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

            Narration narration;
            if (OwnSound(clip) is { } film)
            {
                narration = await TakeSoundAsync(film, text, audio, ct);
            }
            else
            {
                narration = await narrator.SynthesizeAsync(text, audio, clip.NarrationRate, ct);
                if (PaceLearned is { } learned && clip.NarrationRate == 1.0 && Durations.Learned(Durations.WordsPerMinute, text, narration.Duration) is { } pace)
                    learned(Durations.WordsPerMinute = pace);
            }
            await File.WriteAllTextAsync(words, JsonSerializer.Serialize(narration.Words), ct);
            return narration;
        }
        finally
        {
            gate.Release();
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _narrationGates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Told the voice's pace each time a recording changes what it is taken to be, so it can be kept for the next
    /// run. Null means the pace is not learned at all: the stand-in voice speaks at a pace of its own.
    /// </summary>
    public Action<double>? PaceLearned { get; init; }

    /// <summary>The film whose own sound the clip plays instead of narration; null for a narrated clip.</summary>
    private static string? OwnSound(Clip clip) =>
        clip is { OwnSound: true, Visual.UserVideoPath: { } film } && File.Exists(film) ? film : null;

    // The film's sound, cut or padded to the film's length, stands in for the narration: the timeline, the joins and
    // the captions then treat the clip like any other. Words typed for it are spread across it as captions. Free: no
    // service is asked for anything.
    private async Task<Narration> TakeSoundAsync(string film, string text, string audio, CancellationToken ct)
    {
        var length = await ffmpeg.ProbeDurationAsync(film, ct);
        var hasSound = (await ffmpeg.ProbeAsync(["-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", film], ct)).Trim().Length > 0;
        Files.EnsureFolderFor(audio);
        await ffmpeg.RunAsync(FfmpegArgs.FootageSound(hasSound ? film : null, length, audio), Path.GetDirectoryName(audio)!, null, null, ct);
        return new Narration(audio, WavInfo.Duration(audio), Heard(film, text, length) ?? WordTiming.Spread(text, length));
    }

    /// <summary>
    /// The words typed for a film, at the times they were heard said in it. Only when the typed words are as many as
    /// those heard: a word put right keeps its time, but words added or taken away leave nothing to match them to.
    /// </summary>
    private static IReadOnlyList<WordTiming>? Heard(string film, string text, TimeSpan length)
    {
        try
        {
            var heard = JsonSerializer.Deserialize<List<WordTiming>>(File.ReadAllText(Clip.HeardFor(film)));
            var typed = Durations.SplitWords(text);
            return heard is { Count: > 0 } && heard.Count == typed.Length
                ? [.. heard.Select((w, i) => w with { Text = typed[i] }).Where(w => w.Start < length)]
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // never listened to, or the file is damaged: the words are spread across the film
        }
    }

    /// <summary>
    /// Listens to what is said in a film, and gives the words with their times; empty when the film has no sound.
    /// Paid for by the second, so it is done only as part of something the owner asked for.
    /// </summary>
    public async Task<IReadOnlyList<WordTiming>> HearAsync(string film, Hear hear, CancellationToken ct)
    {
        var hasSound = (await ffmpeg.ProbeAsync(["-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", film], ct)).Trim().Length > 0;
        if (!hasSound) return [];
        var speech = Files.NewScratchFile(".wav");
        try
        {
            await ffmpeg.RunAsync(FfmpegArgs.SpeechSound(film, speech), Path.GetDirectoryName(speech)!, null, null, ct);
            return await hear(speech, ct);
        }
        finally
        {
            File.Delete(speech);
        }
    }

    /// <summary>
    /// A few frames spread across a film, small, for a model to look at. They go in a scratch folder of their own,
    /// which the caller deletes once they have been looked at.
    /// </summary>
    public async Task<IReadOnlyList<Frame>> FramesAsync(string film, int count, CancellationToken ct)
    {
        var length = await ffmpeg.ProbeDurationAsync(film, ct);
        var folder = Files.NewScratchFolder("frames");
        var frames = new List<Frame>(count);
        for (var i = 0; i < count; i++)
        {
            // From the middle of each stretch, so the first is past any fade up and the last before the film ends.
            var at = length * ((i + 0.5) / count);
            var path = Path.Combine(folder, $"{i:00}.jpg");
            await ffmpeg.RunAsync(FfmpegArgs.Still(film, at, FrameWidth, path), folder, null, null, ct);
            frames.Add(new Frame(at, path));
        }
        return frames;
    }

    /// <summary>Frames a model looks at are this wide: enough to see what happens, and few tokens.</summary>
    public const int FrameWidth = 512;

    private string AudioPath(Clip clip, string text, string folder)
    {
        // Own sound is keyed by the film as it is on disk, so a replaced film is taken again.
        var source = OwnSound(clip) is { } film
            ? FormattableString.Invariant($"own|{film}|{new FileInfo(film).Length}|{File.GetLastWriteTimeUtc(film).Ticks}")
            : FormattableString.Invariant($"{narrator.GetType().Name}|{clip.NarrationRate:0.###}");
        var key = Files.TextHash($"{source}|{text}")[..16];
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
    /// How long each clip's picture runs in the finished episode: the lengths teasers are cut within. Records any voice
    /// not yet recorded, which an episode ready to export has already done.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, TimeSpan>> ClipLengthsAsync(Episode episode, string folder, CancellationToken ct)
    {
        var narrations = new List<Narration>(episode.Clips.Count);
        foreach (var clip in episode.Clips)
            narrations.Add(await NarrateClipAsync(clip, folder, ct));
        var slots = FfmpegArgs.Timeline([.. narrations.Select(n => n.Duration)]);
        return episode.Clips.Select((c, i) => (c.Id, slots[i].VideoLength)).ToDictionary(p => p.Id, p => p.VideoLength);
    }

    /// <summary>
    /// Renders each teaser as an upright video: its moments cut from the clips' finished pictures (drawn at 1080p
    /// without captions, through the clip cache, and only for the clips the teasers use) and from their sound.
    /// Nothing is generated: no picture, voice or model is asked for anything.
    /// </summary>
    /// <returns>The folder holding them, inside the episode's export folder.</returns>
    public async Task<string> ExportTeasersAsync(Episode episode, string folder, IReadOnlyList<Teaser> teasers, CaptionStyle captions, Brand? brand,
        IProgress<RenderProgress>? progress, CancellationToken ct)
    {
        const double DrawShare = 0.6; // drawing the clips is most of the work; each teaser is then one short encode
        var output = Path.Combine(folder, "export", "teasers");
        Directory.CreateDirectory(output);
        var narrations = new List<Narration>(episode.Clips.Count);
        foreach (var clip in episode.Clips)
            narrations.Add(await NarrateClipAsync(clip, folder, ct));
        var slots = FfmpegArgs.Timeline([.. narrations.Select(n => n.Duration)]);
        var place = episode.Clips.Select((c, i) => (c.Id, i)).ToDictionary(p => p.Id, p => p.i);
        var used = teasers.SelectMany(t => t.Moments).Select(m => place[m.ClipId]).Distinct().Order().ToList();

        var work = Files.NewScratchFolder("teasers");
        ForgetOldClips();
        try
        {
            CopyTitleFont(work);
            var pictures = await DrawClipsAsync(episode, i => narrations[i].Words, i => slots[i], ExportPreset.Hd30, null, used, work, new MediaCache(ClipCacheFolder),
                (activity, fraction, clips) => progress?.Report(new RenderProgress(activity, DrawShare * fraction, clips)), ct);
            var number = episode.Number > 0 ? $"{episode.Number:00}-" : "";
            for (var t = 0; t < teasers.Count; t++)
            {
                var teaser = teasers[t];
                progress?.Report(new RenderProgress($"Cutting teaser {t + 1} of {teasers.Count} · {teaser.Hook}", DrawShare + (1 - DrawShare) * t / teasers.Count));
                var (args, _) = await TeaserCutAsync(teaser, narrations, place, pictures, captions, Bookends(brand), null, work, ct);
                var file = Path.Combine(output, $"{number}{ProjectStore.Slug(episode.Title)}-teaser-{t + 1}.mp4");
                await ffmpeg.RunAsync(args(file), work, teaser.Length, null, ct);
            }
            progress?.Report(new RenderProgress("Done", 1));
            return output;
        }
        finally
        {
            DeleteScratch(work);
        }
    }

    /// <summary>
    /// One teaser, small, to watch on its card before the full render: cut from the same clip pictures the quick
    /// preview draws, so after a preview it takes a moment. Kept in the clip cache, so watching it again is instant.
    /// Nothing is generated.
    /// </summary>
    /// <returns>The video's path.</returns>
    public async Task<string> TeaserDraftAsync(Episode episode, string folder, Teaser teaser, CaptionStyle captions, Brand? brand, CancellationToken ct)
    {
        var narrations = new List<Narration>(episode.Clips.Count);
        foreach (var clip in episode.Clips)
            narrations.Add(await NarrateClipAsync(clip, folder, ct));
        var slots = FfmpegArgs.Timeline([.. narrations.Select(n => n.Duration)]); // as the full teasers lay them out
        var place = episode.Clips.Select((c, i) => (c.Id, i)).ToDictionary(p => p.Id, p => p.i);
        var used = teaser.Moments.Select(m => place[m.ClipId]).Distinct().Order().ToList();
        var work = Files.NewScratchFolder("teaser-draft");
        var cache = new MediaCache(ClipCacheFolder);
        try
        {
            CopyTitleFont(work);
            var pictures = await DrawClipsAsync(episode, i => narrations[i].Words, i => slots[i], Draft, null, used, work, cache, null, ct);
            var (args, made) = await TeaserCutAsync(teaser, narrations, place, pictures, captions, Bookends(brand), ExportPreset.UprightDraft, work, ct);
            // The command names the clip pictures and sounds, whose own names change with what is in them.
            return await cache.GetOrCreateAsync([string.Join('\n', args("teaser.mp4")), .. made], ".mp4", scratch => ffmpeg.RunAsync(args(scratch), work, teaser.Length, null, ct));
        }
        finally
        {
            DeleteScratch(work);
        }
    }

    /// <summary>
    /// Writes a teaser's hook, captions and channel name into the working folder, and gives its command line for any
    /// output path, with the words that decide how it looks besides the command itself.
    /// </summary>
    /// <param name="brand">The kit, when it names a channel; null for no channel bar.</param>
    /// <param name="size">The frame; null for the full upright size.</param>
    private static async Task<(Func<string, IReadOnlyList<string>> Args, IReadOnlyList<string> Made)> TeaserCutAsync(Teaser teaser, IReadOnlyList<Narration> narrations,
        IReadOnlyDictionary<Guid, int> place, string?[] pictures, CaptionStyle captions, Brand? brand, ExportPreset? size, string work, CancellationToken ct)
    {
        // Each moment's words, timed from where the moment falls in the teaser, become its captions.
        var segments = new List<CaptionSegment>();
        var at = TimeSpan.Zero;
        foreach (var moment in teaser.Moments)
        {
            var words = narrations[place[moment.ClipId]].Words.Where(w => w.Start >= moment.Start && w.Start < moment.Start + moment.Length).ToList();
            segments.Add(new CaptionSegment(at - moment.Start, words));
            at += moment.Length;
        }
        var (hook, ass, channel) = (FfmpegArgs.HookLines(teaser.Hook), AssCaptions.Build(segments, captions, portrait: true), brand?.Kit.ChannelName.Trim());
        // A file for each line of the hook, so each can drop in on its own.
        var lines = hook.Split('\n');
        var hookFiles = lines.Select((_, i) => $"teaser_hook_{i}.txt").ToList();
        for (var i = 0; i < lines.Length; i++)
            await File.WriteAllTextAsync(Path.Combine(work, hookFiles[i]), lines[i], ct);
        await File.WriteAllTextAsync(Path.Combine(work, "teaser.ass"), ass, ct);
        if (channel is not null)
            await File.WriteAllTextAsync(Path.Combine(work, "teaser_channel.txt"), channel, ct);
        List<TeaserCut> cuts = [.. teaser.Moments.Select(m => new TeaserCut(pictures[place[m.ClipId]]!, narrations[place[m.ClipId]].AudioPath, m.Start, m.Length))];
        return (to => FfmpegArgs.TeaserVideo(cuts, hookFiles, channel is null ? null : "teaser_channel.txt", "teaser.ass", to, teaser.Fill, size), [hook, ass, channel ?? ""]);
    }

    /// <summary>
    /// One clip on its own, small and with its sound, to watch from its card. Drawn exactly as the quick preview
    /// draws it, so after a preview every clip plays at once; before one, only the clip asked for is drawn. Records the
    /// clip's voice first when that wording has not been recorded, as Audition does.
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

    /// <summary>How long the series intro runs.</summary>
    public static readonly TimeSpan IntroLength = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A short draft of the series intro, with its sound, for the brand kit's own preview. It is made in the scratch
    /// folder, which the app clears at launch.
    /// </summary>
    /// <returns>The video's path.</returns>
    public Task<string> PreviewIntroAsync(Brand brand, int number, CancellationToken ct) => PreviewBookendAsync(true, brand, number, ct);

    /// <summary>A draft of the series outro, as <see cref="PreviewIntroAsync"/>: its full length, with the series' music or the app's own.</summary>
    public Task<string> PreviewOutroAsync(Brand brand, CancellationToken ct) => PreviewBookendAsync(false, brand, 0, ct);

    private async Task<string> PreviewBookendAsync(bool intro, Brand brand, int number, CancellationToken ct)
    {
        var name = intro ? "intro" : "outro";
        var work = Files.NewScratchFolder(name);
        CopyTitleFont(work);
        var sample = new Episode("Your next lesson", "", [], 0) { Number = number };
        var cache = new MediaCache(ClipCacheFolder);
        var sound = await BookendSoundAsync(intro, brand, SeriesKit.PathOf(brand.Folder, brand.Kit.Music), work, cache, ct);
        var picture = await BookendPictureAsync(intro, brand, sample, Draft, intro ? IntroLength : brand.Kit.Outro, work, cache, ct);
        var output = Path.Combine(work, $"{name}-preview.mp4");
        await ffmpeg.RunAsync(FfmpegArgs.Mux(picture, sound.AudioPath, output), work, null, null, ct);
        return output;
    }

    /// <summary>
    /// The sound of the series intro (its sting; one made up here, timed to the swarm, when the series has none) or
    /// outro (the music: <paramref name="music"/>, or a tune made up here when there is none; no music plays under the
    /// clips). Kept in the clip cache, not the working folder, because the preview's waveform reads it after the render
    /// has finished.
    /// </summary>
    private async Task<Narration> BookendSoundAsync(bool intro, Brand brand, string? music, string work, MediaCache cache, CancellationToken ct)
    {
        var length = intro ? IntroLength : brand.Kit.Outro;
        var own = intro ? SeriesKit.PathOf(brand.Folder, brand.Kit.Sting) : music is not null && File.Exists(music) ? Path.GetFullPath(music) : null;
        IReadOnlyList<string> Args(string from, string to) => intro ? FfmpegArgs.FootageSound(from, length, to) : FfmpegArgs.OutroMusic(from, length, to);
        var sound = own is null
            ? await cache.GetOrCreateAsync([intro ? MadeUpSting : MadeUpOutro, length.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)], ".wav",
                scratch => File.WriteAllBytesAsync(scratch, intro ? Synth.Sting(length) : Synth.OutroTune(length), ct))
            : await cache.GetOrCreateAsync([string.Join('\n', Args("own", "out.wav")), Stamp(own)], ".wav",
                scratch => ffmpeg.RunAsync(Args(own, scratch), work, null, null, ct));
        return new Narration(sound, length, []);
    }

    // Names the made-up sting in the clip cache. Change it when the sting changes, so the new one is made.
    private const string MadeUpSting = "made-up sting 2", MadeUpOutro = "made-up outro 1";

    // Names the swarm in the clip cache. Change it when the swarm changes, so intros are drawn again.
    private const string SwarmVersion = "swarm 1";

    /// <summary>The most the swarm is drawn at; a larger frame has it enlarged, which its soft glow hides.</summary>
    private static readonly ExportPreset SwarmLimit = new(1280, 720, 30);

    /// <summary>
    /// The picture of the series intro or outro for an episode, <paramref name="length"/> long. Cached like a clip's,
    /// so an unchanged kit and title cost nothing to draw again.
    /// </summary>
    private async Task<string> BookendPictureAsync(bool intro, Brand brand, Episode episode, ExportPreset preset, TimeSpan length, string work, MediaCache cache, CancellationToken ct)
    {
        var (look, words, draw, fileOf) = await BookendCardAsync(intro, brand, episode, preset, length, work, ct);
        List<string> made = [string.Join('\n', draw("clip.mp4")), .. words, .. new[] { look.Logo, look.Mascot }.OfType<string>().Select(Stamp)];
        var card = await cache.GetOrCreateAsync(made, ".mp4", scratch => ffmpeg.RunAsync(draw(scratch), work, length, null, ct));
        if (!intro) return card;

        // The intro opens on the swarm gathering into this card. The card's own name in the cache says what is on it.
        var scale = Math.Min(1.0, Math.Min((double)SwarmLimit.Width / preset.Width, (double)SwarmLimit.Height / preset.Height));
        var size = new ExportPreset((int)(preset.Width * scale) / 2 * 2, (int)(preset.Height * scale) / 2 * 2, preset.Fps);
        return await cache.GetOrCreateAsync([SwarmVersion, card, $"{size.Width}x{size.Height}", string.Join('\n', FfmpegArgs.SwarmIntroVideo(look, "card.mp4", size, length, preset, "clip.mp4"))], ".mp4", async scratch =>
        {
            var maskFile = Path.Combine(work, "intro_mask.rgb");
            await ffmpeg.RunAsync(FfmpegArgs.IntroMask(look, fileOf("channel")!, fileOf("tagline"), fileOf("title")!, size, maskFile), work, null, null, ct);
            var accent = Convert.FromHexString(look.Accent.TrimStart('#'));
            var swarm = new Swarm(await System.IO.File.ReadAllBytesAsync(maskFile, ct), size.Width, size.Height, (accent[0], accent[1], accent[2]));
            if (swarm.Count == 0)
            {
                System.IO.File.Copy(card, scratch, overwrite: true); // nothing bright to gather into: the card as it is
                return;
            }
            await ffmpeg.RunAsync(FfmpegArgs.SwarmIntroVideo(look, card, size, length, preset, scratch), work, length, null, ct, async (input, token) =>
            {
                var frame = new byte[size.Width * size.Height * 3];
                var frames = (int)Math.Round(length.TotalSeconds * preset.Fps);
                for (var f = 0; f < frames; f++)
                {
                    swarm.Draw((double)f / preset.Fps, 1.0 / preset.Fps, frame);
                    await input.WriteAsync(frame, token);
                }
            });
        });
    }

    /// <summary>
    /// The intro's or outro's words, written to the working folder for FFmpeg to read, with its look and how to draw its
    /// card into a file. A line with nothing to say is left out.
    /// </summary>
    private async Task<(BookendLook Look, string[] Words, Func<string, IReadOnlyList<string>> Args, Func<string, string?> File)> BookendCardAsync(
        bool intro, Brand brand, Episode episode, ExportPreset preset, TimeSpan length, string work, CancellationToken ct)
    {
        var (kit, name) = (brand.Kit, intro ? "intro" : "outro");
        var lines = intro
            ? new (string Key, string Text)[] { ("channel", brand.ChannelName), ("tagline", kit.Tagline), ("title", TitleLine(episode)) }
            : [("channel", brand.ChannelName), ("next", NextTime(episode) is { } next ? $"Next time: {next}" : "")];
        string? File(string key) => lines.First(l => l.Key == key).Text.Trim() is { Length: > 0 } ? $"{name}_{key}.txt" : null;
        foreach (var (key, text) in lines.Where(l => l.Text.Trim().Length > 0))
            await System.IO.File.WriteAllTextAsync(Path.Combine(work, $"{name}_{key}.txt"), text.Trim(), ct);

        // A series without its own logo or mascot gets the app's own, in its colour.
        var accent = kit.Captions.AccentColor;
        var look = new BookendLook(accent,
            SeriesKit.PathOf(brand.Folder, kit.Logo) ?? await MadeUpPictureAsync(logo: true, accent, work, ct),
            SeriesKit.PathOf(brand.Folder, kit.Mascot) ?? await MadeUpPictureAsync(logo: false, accent, work, ct),
            kit.Layout);
        IReadOnlyList<string> Args(string to) => intro
            ? FfmpegArgs.IntroVideo(look, File("channel")!, File("tagline"), File("title")!, length, preset, to)
            : FfmpegArgs.OutroVideo(look, File("channel")!, File("next"), length, preset, to);
        return (look, [.. lines.Select(l => l.Text)], Args, File);
    }

    /// <summary>The app's own logo or mascot in the series colour (see <see cref="Emblem"/>), made once for each colour and kept in the clip cache.</summary>
    private async Task<string> MadeUpPictureAsync(bool logo, string accent, string work, CancellationToken ct)
    {
        var (width, height) = logo ? (Emblem.LogoSize, Emblem.LogoSize) : (Emblem.MascotWidth, Emblem.MascotHeight);
        return await new MediaCache(ClipCacheFolder).GetOrCreateAsync([MadeUpPictures, logo ? "logo" : "mascot", accent], ".png", async scratch =>
        {
            var colour = Convert.FromHexString(accent.TrimStart('#'));
            var pixels = Path.Combine(work, logo ? "made-up-logo.rgba" : "made-up-mascot.rgba");
            await File.WriteAllBytesAsync(pixels, logo ? Emblem.Logo((colour[0], colour[1], colour[2])) : Emblem.Mascot((colour[0], colour[1], colour[2])), ct);
            await ffmpeg.RunAsync(FfmpegArgs.Picture(pixels, width, height, scratch), work, null, null, ct);
        });
    }

    // Names the made-up logo and mascot in the clip cache. Change it when either changes, so they are drawn again.
    private const string MadeUpPictures = "made-up pictures 1";

    /// <summary>
    /// One still of the intro or outro once all its words are in, drawn in a moment and costing nothing, so the Intro
    /// step can show each change to the brand kit as it is made. Made in the scratch folder.
    /// </summary>
    /// <returns>The picture's path.</returns>
    public async Task<string> BookendStillAsync(bool intro, Brand brand, int number, CancellationToken ct)
    {
        var name = intro ? "intro" : "outro";
        var work = Files.NewScratchFolder($"{name}-still");
        CopyTitleFont(work);
        var sample = new Episode("Your next lesson", "", [], 0) { Number = number };
        var length = intro ? IntroLength : brand.Kit.Outro;
        var (_, _, args, _) = await BookendCardAsync(intro, brand, sample, StillSize, length, work, ct);
        var output = Path.Combine(work, $"{name}.png");
        await ffmpeg.RunAsync(FfmpegArgs.Still(args("card.mp4"), intro ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(2), output), work, null, null, ct);
        return output;
    }

    // Sharp enough for the Intro step's screen, and few frames to work through before the one taken.
    private static readonly ExportPreset StillSize = new(1280, 720, 10);
    /// <summary>The intro's line for the episode, "#7 · Walking up stairs"; never empty, as the intro always draws it.</summary>
    private static string TitleLine(Episode episode)
    {
        var title = episode.Title.Trim() is { Length: > 0 } named ? named : "A new lesson";
        return episode.Number > 0 ? $"#{episode.Number} · {title}" : title;
    }

    /// <summary>What the outro says comes next: the title of a "Next time" clip, when the episode has one.</summary>
    private static string? NextTime(Episode episode) => episode.Clips.LastOrDefault(c => c.Beat == "Next time")?.Title;

    // Title cards and bookends read their font from the working folder: this FFmpeg build has no font lookup by name.
    private static void CopyTitleFont(string work) =>
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeuib.ttf"), Path.Combine(work, FfmpegArgs.TitleFontFile), overwrite: true);

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
