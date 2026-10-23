namespace PoAutoRobo.Core.Render;

// The series intro and outro every episode is wrapped in, and the stills and drafts of them the Intro step shows.
public sealed partial class EpisodeBuilder
{
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

    /// <summary>
    /// The intro's sting or the outro's music on its own, exactly as it will play: the series' own cut to the intro's
    /// length or repeated to fill the outro, or the app's made-up one. For the Intro step to listen to; free.
    /// </summary>
    /// <returns>The sound file's path, in the clip cache.</returns>
    public async Task<string> BookendSoundAsync(bool intro, Brand brand, CancellationToken ct)
    {
        var work = Files.NewScratchFolder(intro ? "sting" : "outro-music");
        var sound = await BookendSoundAsync(intro, brand, SeriesKit.PathOf(brand.Folder, brand.Kit.Music), work, new MediaCache(ClipCacheFolder), ct);
        return sound.AudioPath;
    }

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
}
