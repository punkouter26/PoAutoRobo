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

            var narration = OwnSound(clip) is { } film
                ? await TakeSoundAsync(film, text, audio, ct)
                : await narrator.SynthesizeAsync(text, audio, clip.NarrationRate, ct);
            await File.WriteAllTextAsync(words, JsonSerializer.Serialize(narration.Words), ct);
            return narration;
        }
        finally
        {
            _narrationGate.Release();
        }
    }

    private readonly SemaphoreSlim _narrationGate = new(1, 1);

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
        return new Narration(audio, WavInfo.Duration(audio), WordTiming.Spread(text, length));
    }

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
        await ffmpeg.RunAsync(["-ss", (length.TotalSeconds / 2).ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-i", videoPath, "-frames:v", "1", "-vf", "scale=640:-2", "-y", poster],
            Path.GetDirectoryName(Path.GetFullPath(videoPath))!, null, null, ct);
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
            var pictures = await DrawClipsAsync(episode, narrations, i => slots[i], ExportPreset.Hd30, null, used, work, new MediaCache(ClipCacheFolder),
                (activity, fraction, clips) => progress?.Report(new RenderProgress(activity, DrawShare * fraction, clips)), ct);
            var number = episode.Number > 0 ? $"{episode.Number:00}-" : "";
            var channel = brand is { Kit.ChannelName: var name } && name.Trim().Length > 0 ? name.Trim() : null;
            for (var t = 0; t < teasers.Count; t++)
            {
                var teaser = teasers[t];
                progress?.Report(new RenderProgress($"Cutting teaser {t + 1} of {teasers.Count} · {teaser.Hook}", DrawShare + (1 - DrawShare) * t / teasers.Count));
                // Each moment's words, timed from where the moment falls in the teaser, become its captions.
                var segments = new List<CaptionSegment>();
                var at = TimeSpan.Zero;
                foreach (var moment in teaser.Moments)
                {
                    var words = narrations[place[moment.ClipId]].Words.Where(w => w.Start >= moment.Start && w.Start < moment.Start + moment.Length).ToList();
                    segments.Add(new CaptionSegment(at - moment.Start, words));
                    at += moment.Length;
                }
                await File.WriteAllTextAsync(Path.Combine(work, "teaser_hook.txt"), FfmpegArgs.HookLines(teaser.Hook), ct);
                await File.WriteAllTextAsync(Path.Combine(work, "teaser.ass"), AssCaptions.Build(segments, captions, portrait: true), ct);
                if (channel is not null)
                    await File.WriteAllTextAsync(Path.Combine(work, "teaser_channel.txt"), channel, ct);
                List<TeaserCut> cuts = [.. teaser.Moments.Select(m => new TeaserCut(pictures[place[m.ClipId]]!, narrations[place[m.ClipId]].AudioPath, m.Start, m.Length))];
                var file = Path.Combine(output, $"{number}{ProjectStore.Slug(episode.Title)}-teaser-{t + 1}.mp4");
                await ffmpeg.RunAsync(FfmpegArgs.TeaserVideo(cuts, "teaser_hook.txt", channel is null ? null : "teaser_channel.txt", "teaser.ass", file), work, teaser.Length, null, ct);
            }
            progress?.Report(new RenderProgress("Done", 1));
            return output;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Leftover scratch in the temp folder is harmless; it must not turn finished teasers into an error.
            }
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
            var ends = brand is { Kit.ChannelName: var channel } && channel.Trim().Length > 0 ? brand : null;
            var first = ends is null ? 0 : 1;
            List<Narration> sounds = ends is null ? [.. narrations]
                : [await BookendSoundAsync(true, ends, work, ct), .. narrations, await BookendSoundAsync(false, ends, work, ct)];
            var slots = FfmpegArgs.Timeline([.. sounds.Select(n => n.Duration)]);
            var segments = narrations.Select((n, i) => new CaptionSegment(slots[i + first].Start, n.Words)).ToList();

            CopyTitleFont(work);
            var pictures = await DrawClipsAsync(episode, narrations, i => slots[i + first], preset, captions, [.. Enumerable.Range(0, count)], work, cache,
                (activity, fraction, clips) => progress?.Report(new RenderProgress(activity, VoiceShare + DrawShare * fraction, clips)), ct);

            // The bookends' pictures run exactly their slots, which the crossfade into the next clip shortens a little.
            List<string> joined = ends is null ? [.. pictures.OfType<string>()]
                : [await BookendPictureAsync(true, ends, episode, preset, slots[0].VideoLength, work, cache, ct), .. pictures.OfType<string>(),
                    await BookendPictureAsync(false, ends, episode, preset, slots[^1].VideoLength, work, cache, ct)];
            await File.WriteAllTextAsync(Path.Combine(work, "clips.txt"), FfmpegArgs.ClipList(joined), ct);
            var total = slots[^1].Start + slots[^1].VideoLength;
            const string Joining = "Joining the clips and levelling the sound";
            var joinProgress = progress is null ? null : new Relay<double>(p => progress.Report(new RenderProgress(Joining, VoiceShare + DrawShare + (1 - VoiceShare - DrawShare) * p)));
            var music = episode.MusicPath is { } track && File.Exists(track) ? Path.GetFullPath(track) : null;
            await ffmpeg.RunAsync(FfmpegArgs.Join("clips.txt", [.. sounds.Select(n => n.AudioPath)], partial, music), work, total, joinProgress, ct);

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

    /// <summary>
    /// Encodes the finished picture of each clip asked for (captions and fades included), several at a time, through
    /// the clip cache. A join then only copies them, so the slow work is spread across the processor's cores and
    /// nothing is encoded twice.
    /// </summary>
    /// <param name="slotOf">The slot each clip fills on the timeline, by its place in the episode.</param>
    /// <param name="which">The places of the clips to draw; the others are left null.</param>
    /// <param name="report">Told how the drawing is going: in words, how far through it is (0 to 1), and how far each clip has got.</param>
    private async Task<string?[]> DrawClipsAsync(Episode episode, IReadOnlyList<Narration> narrations, Func<int, Slot> slotOf, ExportPreset preset, CaptionStyle? captions,
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
            string? captionFile = null;
            if (captions is not null)
            {
                captionFile = $"captions_{i:00}.ass"; // timed from the start of this clip
                // The first clip of each beat names it with a strap, so the viewer sees where the episode has got to.
                // ponytail: burned in with the captions, so the preview (captions drawn live) shows none; draw it there too if it is missed.
                var strap = clip.Beat is { } beat && (i == 0 || episode.Clips[i - 1].Beat != beat) ? beat : null;
                var text = AssCaptions.Build([new CaptionSegment(TimeSpan.Zero, narrations[i].Words)], captions, preset.Portrait, strap);
                await File.WriteAllTextAsync(Path.Combine(work, captionFile), text, token);
                made.Add(text);
            }
            // The camera move comes from the clip itself, so a clip keeps its move when the order changes.
            IReadOnlyList<string> Args(string to) =>
                panels.Count == 2 && clip.Visual.Kind == VisualKind.Parallax ? FfmpegArgs.ParallaxVideo(panels[0], panels[1], slot.VideoLength, preset, to, captionFile)
                : panels.Count > 1 ? FfmpegArgs.PanelsVideo(panels, slot.VideoLength, preset, to, captionFile)
                : FfmpegArgs.ClipVideo(source, input, slot.VideoLength, preset, to, captionFile, clip.Id.ToByteArray()[0] % 4);
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
    public async Task<string> PreviewIntroAsync(Brand brand, int number, CancellationToken ct)
    {
        var work = Files.NewScratchFolder("intro");
        CopyTitleFont(work);
        var sample = new Episode("Your next lesson", "", [], 0) { Number = number };
        var cache = new MediaCache(ClipCacheFolder);
        var sound = await BookendSoundAsync(true, brand, work, ct);
        var picture = await BookendPictureAsync(true, brand, sample, Draft, IntroLength, work, cache, ct);
        var output = Path.Combine(work, "intro-preview.mp4");
        await ffmpeg.RunAsync(["-i", picture, "-i", sound.AudioPath, "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-shortest", "-y", output], work, null, null, ct);
        return output;
    }

    /// <summary>The sound of the series intro (its sting, or silence) or outro (silence: the music plays on under it), in the working folder.</summary>
    private async Task<Narration> BookendSoundAsync(bool intro, Brand brand, string work, CancellationToken ct)
    {
        var length = intro ? IntroLength : brand.Kit.Outro;
        var sound = Path.Combine(work, intro ? "intro.wav" : "outro.wav");
        await ffmpeg.RunAsync(FfmpegArgs.FootageSound(intro ? SeriesKit.PathOf(brand.Folder, brand.Kit.Sting) : null, length, sound), work, null, null, ct);
        return new Narration(sound, length, []);
    }

    /// <summary>
    /// The picture of the series intro or outro for an episode, <paramref name="length"/> long. Cached like a clip's,
    /// so an unchanged kit and title cost nothing to draw again.
    /// </summary>
    private async Task<string> BookendPictureAsync(bool intro, Brand brand, Episode episode, ExportPreset preset, TimeSpan length, string work, MediaCache cache, CancellationToken ct)
    {
        var (kit, name) = (brand.Kit, intro ? "intro" : "outro");
        // The words, read by name from the working folder like a title card's; a line with nothing to say is left out.
        var lines = intro
            ? new (string Key, string Text)[] { ("channel", brand.ChannelName), ("tagline", kit.Tagline), ("title", episode.Number > 0 ? $"#{episode.Number} · {episode.Title}" : episode.Title) }
            : [("channel", brand.ChannelName), ("next", NextTime(episode) is { } next ? $"Next time: {next}" : "")];
        string? File(string key) => lines.First(l => l.Key == key).Text.Trim() is { Length: > 0 } ? $"{name}_{key}.txt" : null;
        foreach (var (key, text) in lines.Where(l => l.Text.Trim().Length > 0))
            await System.IO.File.WriteAllTextAsync(Path.Combine(work, $"{name}_{key}.txt"), text.Trim(), ct);

        var look = new BookendLook(kit.Captions.AccentColor, SeriesKit.PathOf(brand.Folder, kit.Logo), SeriesKit.PathOf(brand.Folder, kit.Mascot));
        IReadOnlyList<string> Args(string to) => intro
            ? FfmpegArgs.IntroVideo(look, File("channel")!, File("tagline"), File("title")!, length, preset, to)
            : FfmpegArgs.OutroVideo(look, File("channel")!, File("next"), length, preset, to);
        List<string> made = [string.Join('\n', Args("clip.mp4")), .. lines.Select(l => l.Text), .. new[] { look.Logo, look.Mascot }.OfType<string>().Select(Stamp)];
        return await cache.GetOrCreateAsync(made, ".mp4", scratch => ffmpeg.RunAsync(Args(scratch), work, length, null, ct));
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
