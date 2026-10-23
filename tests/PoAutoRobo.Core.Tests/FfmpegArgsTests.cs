using System.Globalization;

namespace PoAutoRobo.Core.Tests;

public sealed class FfmpegArgsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static string Lines(IEnumerable<string> args) => string.Join('\n', args);

    [Fact]
    public Task The_timeline_follows_the_narration_and_a_teaser_is_laid_out_upright_over_a_blurred_copy_of_its_moments()
    {
        Timeline_follows_the_narration_with_audio_overlapping_at_each_join();
        return A_teaser_joins_its_moments_and_lays_them_out_for_the_narrow_frame();
    }

    private void Timeline_follows_the_narration_with_audio_overlapping_at_each_join()
    {
        var slots = FfmpegArgs.Timeline([S(20), S(30), S(15)]);

        Assert.Equal([S(0), S(19.85), S(49.70)], slots.Select(s => s.Start));
        // Each picture runs exactly its share of the timeline, so finished clips can be joined without re-encoding.
        Assert.Equal([S(19.85), S(29.85), S(15)], slots.Select(s => s.VideoLength));
        Assert.Equal(S(65 - 2 * 0.15), slots[^1].Start + slots[^1].VideoLength); // the crossfaded narration length
    }

    [Fact]
    public Task Footage_that_keeps_its_own_sound_gives_a_track_as_long_as_the_film_with_its_typed_words_spread_across_it()
    {
        var words = WordTiming.Spread("It falls over", S(6));
        Assert.Equal(["It", "falls", "over"], words.Select(w => w.Text));
        Assert.Equal(0, words[0].Start.TotalSeconds);
        Assert.Equal(6, (words[^1].Start + words[^1].Duration).TotalSeconds, precision: 6);
        Assert.Empty(WordTiming.Spread("", S(6))); // nothing typed: no captions
        // Listened to as speech to text reads best, and a frame taken at its own size when no width is given.
        Assert.Equal(["-i", "fall.mp4", "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-y", "speech.wav"], FfmpegArgs.SpeechSound("fall.mp4", "speech.wav"));
        Assert.Equal(["-ss", "2.500", "-i", "scene.mp4", "-frames:v", "1", "-y", "frame.png"], FfmpegArgs.Still("scene.mp4", S(2.5), 0, "frame.png"));
        Assert.Contains("scale=512:-2", FfmpegArgs.Still("fall.mp4", S(1), 512, "f.jpg"));

        // A film with no sound track still gives a track of its length, so the timeline and the joins hold.
        return Verify(Lines(FfmpegArgs.FootageSound("fall.mp4", S(6), "own.wav")) + "\n--- a film with no sound ---\n" + Lines(FfmpegArgs.FootageSound(null, S(6), "own.wav")));
    }

    [Fact]
    public Task The_series_intro_and_outro_are_drawn_from_the_brand_kit_and_encoded_like_every_clip()
    {
        var full = new BookendLook("#22CC88", "logo.png", "mascot.png");
        var bare = new BookendLook("#FFD400", null, null); // nothing set up but the channel's name
        var intro = Lines(FfmpegArgs.IntroVideo(full, "intro_channel.txt", "intro_tagline.txt", "intro_title.txt", S(5), ExportPreset.Hd30, "intro.mp4"));
        var outro = Lines(FfmpegArgs.OutroVideo(full, "outro_channel.txt", "outro_next.txt", S(15), ExportPreset.Hd30, "outro.mp4"));

        // Joined by copying, so they must be encoded exactly as the clips are, and every word is drawn, never run.
        var clip = Lines(FfmpegArgs.ClipVideo(ClipSource.Image, "panel.png", S(5), ExportPreset.Hd30, "clip.mp4"));
        var swarm = Lines(FfmpegArgs.SwarmIntroVideo(full, "intro.mp4", new ExportPreset(1280, 720, 30), S(5), ExportPreset.Hd30, "swarm.mp4"));
        Assert.All(new[] { intro, swarm }, card =>
            Assert.EndsWith(clip[clip.IndexOf("-c:v", StringComparison.Ordinal)..clip.IndexOf("-y", StringComparison.Ordinal)], card[..card.IndexOf("-y", StringComparison.Ordinal)], StringComparison.Ordinal));
        // The swarm reads its frames from the app, at their own size, and lands on the card as the sting hits.
        Assert.Contains("-f\nrawvideo\n-pix_fmt\nrgb24\n-s\n1280x720\n-r\n30\n-i\npipe:0", swarm);
        Assert.Contains("fade=t=in:st=2.200:d=0.6:alpha=1", swarm);
        Assert.All(new[] { intro, outro }, card => Assert.Equal(card.Split("drawtext").Length - 1, card.Split("expansion=none").Length - 1));

        return Verify(
            $"--- intro ---\n{intro}\n" +
            $"--- the swarm gathering into it ---\n{swarm}\n" +
            $"--- where the swarm lands ---\n{Lines(FfmpegArgs.IntroMask(full, "intro_channel.txt", "intro_tagline.txt", "intro_title.txt", new ExportPreset(1280, 720, 30), "mask.rgb"))}\n" +
            $"--- intro, channel name only ---\n{Lines(FfmpegArgs.IntroVideo(bare, "intro_channel.txt", null, "intro_title.txt", S(5), ExportPreset.Hd30, "intro.mp4"))}\n" +
            $"--- intro laid out about its centre: the mascot above the words ---\n{Lines(FfmpegArgs.IntroVideo(full with { Layout = IntroLayout.Centre }, "intro_channel.txt", "intro_tagline.txt", "intro_title.txt", S(5), ExportPreset.Hd30, "intro.mp4"))}\n" +
            $"--- intro with its words on a band across the lower third ---\n{Lines(FfmpegArgs.IntroVideo(full with { Layout = IntroLayout.Lower }, "intro_channel.txt", "intro_tagline.txt", "intro_title.txt", S(5), ExportPreset.Hd30, "intro.mp4"))}\n" +
            $"--- outro ---\n{outro}\n" +
            $"--- outro, channel name only ---\n{Lines(FfmpegArgs.OutroVideo(bare, "outro_channel.txt", null, S(8), ExportPreset.Hd30, "outro.mp4"))}");
    }

    [Fact]
    public Task Video_clip_is_fitted_and_its_last_frame_held_and_can_be_seen_through_the_robots_eyes()
    {
        var eye = Lines(FfmpegArgs.ClipVideo(ClipSource.Video, "lab.mp4", S(12), ExportPreset.Hd30, "clip_01.mp4", "captions_01.ass", eye: new RobotEye("eye_01.txt", Fault: false)));
        var fault = Lines(FfmpegArgs.ClipVideo(ClipSource.Video, "lab.mp4", S(12), ExportPreset.Hd30, "clip_01.mp4", eye: new RobotEye("eye_01.txt", Fault: true)));

        Assert.Contains("eye_01.txt:expansion=none", eye); // the readout's words are the user's: drawn, never run
        Assert.True(eye.IndexOf("pseudocolor", StringComparison.Ordinal) < eye.IndexOf("ass=captions_01.ass", StringComparison.Ordinal)); // captions stay readable on top
        Assert.DoesNotContain("FAULT", eye);
        Assert.Contains("FAULT", fault); // a beat where it went wrong glitches and says so
        return Verify(Lines(FfmpegArgs.ClipVideo(ClipSource.Video, "lab.mp4", S(12), ExportPreset.Uhd60, "clip_01.mp4")) + "\n--- through the robot's eyes ---\n" + eye);
    }

    [Fact]
    public Task Title_card_clip_is_drawn_from_a_text_file()
    {
        var args = Lines(FfmpegArgs.ClipVideo(ClipSource.TitleCard, "title_02.txt", S(15), ExportPreset.Hd30, "clip_02.mp4"));

        Assert.Contains("expansion=none", args); // the title is the user's text: percent codes in it must be drawn, never run
        return Verify(args);
    }

    [Fact]
    public Task Captioned_clip_burns_its_own_captions_and_fades_in_and_out() =>
        Verify(Lines(FfmpegArgs.ClipVideo(ClipSource.Image, "panel.png", S(20), ExportPreset.Hd30, "clip_00.mp4", "captions_00.ass")));

    [Fact]
    public Task Join_copies_the_finished_pictures_and_crossfades_and_normalises_the_narration()
    {
        // One clip has nothing to fade into, and is still levelled.
        var alone = Lines(FfmpegArgs.Join("clips.txt", ["a0.wav"], "out.mp4"));
        Assert.DoesNotContain("acrossfade", alone);
        Assert.Contains("[s0]loudnorm", alone);

        // A whoosh into each new beat, loudest on the cut; one too near the start to be heard whole is left out.
        var swept = FfmpegArgs.Join("clips.txt", ["a0.wav", "a1.wav", "a2.wav"], "out.mp4", "bed.mp3", "whoosh.wav", [S(0.3), S(19.85), S(49.7)]).ToList();
        var graph = swept[swept.IndexOf("-filter_complex") + 1];
        Assert.Equal("whoosh.wav", swept[swept.IndexOf("bed.mp3") + 2]);
        Assert.Contains("[5:a]aformat=sample_rates=48000:channel_layouts=stereo,asplit=2[w0][w1]", graph);
        Assert.Contains("[w0]adelay=delays=19250:all=1[d0]", graph);
        Assert.Contains("[a2][d0][d1]amix=inputs=3:duration=first:normalize=0[swept]", graph);
        Assert.Contains("[swept]asplit[voice][key]", graph); // the music dips under the whooshes as under the voice

        return Verify(Lines(FfmpegArgs.Join("clips.txt", ["a0.wav", "a1.wav", "a2.wav"], "out.mp4")));
    }

    private Task A_teaser_joins_its_moments_and_lays_them_out_for_the_narrow_frame()
    {
        TeaserCut[] cuts = [new("clip_a.mp4", "a.wav", S(2), S(6)), new("clip_b.mp4", "b.wav", S(0), S(4))];
        string[] hook = ["teaser_hook_0.txt", "teaser_hook_1.txt"];
        var args = Lines(FfmpegArgs.TeaserVideo(cuts, hook, "teaser_channel.txt", "teaser.ass", "teaser.mp4"));

        // Each moment is a stretch of a finished clip and of its sound; the words are drawn, never run.
        Assert.Contains("-ss\n2.000\n-t\n6.000\n-i\nclip_a.mp4\n-ss\n2.000\n-t\n6.000\n-i\na.wav", args);
        Assert.Equal(3, args.Split("expansion=none").Length - 1);
        // Each line of the hook falls in a moment after the one above it and bounces to rest.
        Assert.Contains("(t-0.15)", args);
        Assert.Contains("(t-0.27)", args);
        // A hook is broken into short lines, so eight words fit the narrow frame.
        Assert.Equal("It fell twice, then\nit climbed all three", FfmpegArgs.HookLines("It fell twice, then it climbed all three"));
        CaptionSegment[] words = [new(S(0), [new WordTiming("Hello.", S(0), S(0.4))])];
        var upright = AssCaptions.Build(words, new CaptionStyle(), portrait: true);
        Assert.Contains("PlayResX: 1080", upright);
        Assert.Contains("PlayResY: 1920", upright);
        Assert.Contains("WrapStyle: 0", upright);
        Assert.Contains("PlayResX: 1920", AssCaptions.Build(words, new CaptionStyle())); // the master is unchanged

        // Filled to the frame there is no blurred copy behind; a draft to watch is the same teaser, smaller.
        var filled = Lines(FfmpegArgs.TeaserVideo(cuts, hook, "teaser_channel.txt", "teaser.ass", "teaser.mp4", fill: true, ExportPreset.UprightDraft));
        Assert.DoesNotContain("boxblur", filled);

        return Verify(args + "\n--- no channel name, no captions ---\n" + Lines(FfmpegArgs.TeaserVideo(cuts[..1], hook[..1], null, null, "teaser.mp4"))
            + "\n--- filled to the frame, as a small draft ---\n" + filled);
    }

    [Fact]
    public void Every_kind_of_clip_is_encoded_identically_so_they_can_be_joined_by_copying_and_numbers_use_a_dot_on_any_system()
    {
        var before = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE"); // a comma-decimal system
        try
        {
            Assert.All(Enum.GetValues<ClipSource>(), source =>
            {
                var args = string.Join(' ', FfmpegArgs.ClipVideo(source, "in", S(20.35), ExportPreset.Hd30, "o.mp4"));

                Assert.Contains("-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p -r 30 -video_track_timescale 90000", args);
                Assert.Contains("fade=t=in:st=0:d=0.250", args);
                Assert.Contains("fade=t=out:st=20.100:d=0.250", args);
                Assert.Contains("-t 20.350", args);
            });
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = before;
        }
    }

    [Fact]
    public void The_swarm_gathers_into_the_card_as_the_made_up_sting_hits()
    {
        // A card that is black but for one white square in the middle.
        const int W = 64, H = 36;
        var card = new byte[W * H * 3];
        for (var y = 14; y < 22; y++)
            for (var x = 28; x < 36; x++)
                card[(y * W + x) * 3] = card[(y * W + x) * 3 + 1] = card[(y * W + x) * 3 + 2] = 255;
        var swarm = new Swarm(card, W, H, (255, 212, 0));
        Assert.Equal(64, swarm.Count); // a point for every lit pixel, while there are few
        Assert.Equal(0, new Swarm(new byte[W * H * 3], W, H, (255, 212, 0)).Count); // nothing to gather into

        // Before the hit the light is all over the frame; just after it, on the square.
        var frame = new byte[W * H * 3];
        double OnSquare()
        {
            // Measured above the dimmest pixel: the blow's flash lights the whole frame for a moment.
            var floor = Enumerable.Range(0, W * H).Min(i => frame[i * 3]);
            double inside = 0, all = 0;
            for (var i = 0; i < W * H; i++)
            {
                var (x, y) = (i % W, i / W);
                all += frame[i * 3] - floor;
                if (x is >= 27 and < 37 && y is >= 13 and < 23) inside += frame[i * 3] - floor;
            }
            return inside / Math.Max(1, all);
        }
        var fps = 30.0;
        var early = 0.0;
        for (var f = 0; f / fps < Synth.Hit.TotalSeconds + 0.6; f++)
        {
            swarm.Draw(f / fps, 1 / fps, frame);
            if (f == 15) early = OnSquare();
        }
        Assert.InRange(early, 0, 0.5);
        Assert.InRange(OnSquare(), 0.6, 1);

        // The made-up sting is as long as the intro and loudest at the hit; the whoosh is loudest where it is laid on a cut.
        var sting = Synth.Sting(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), WavInfo.Duration(Write("sting.wav", sting)));
        var peaks = WavInfo.Peaks(Write("sting.wav", sting), 50);
        Assert.InRange(Array.IndexOf(peaks, peaks.Max()) / 10.0, Synth.Hit.TotalSeconds - 0.1, Synth.Hit.TotalSeconds + 0.3);
        Assert.Equal(sting, Synth.Sting(TimeSpan.FromSeconds(5))); // the same every time, so it caches
        var rush = WavInfo.Peaks(Write("whoosh.wav", Synth.Whoosh()), 9);
        Assert.InRange(Array.IndexOf(rush, rush.Max()) / 10.0, 0.4, 0.7);

        // Heard band by band, as the audition draws a voice: silence before the hit, the low thump strongest on it.
        var bands = WavInfo.Spectrum(Write("sting.wav", sting), 30, 16);
        Assert.Equal(150, bands.Length);
        Assert.All(bands, frame => Assert.All(frame, strength => Assert.InRange(strength, 0f, 1f)));
        var onHit = bands[(int)(Synth.Hit.TotalSeconds * 30) + 2];
        Assert.True(onHit[0] > 0.5f && onHit[0] > bands[1][0], $"{onHit[0]} on the hit, {bands[1][0]} at the start");
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ---- Finding FFmpeg ----

    [Fact]
    public void Ffmpeg_is_only_taken_from_absolute_folders_on_the_path()
    {
        var real = Path.Combine(_folder, "bin");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "ffmpeg.exe"), "");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, real);

        Assert.Null(FfmpegRunner.LocateIn(relative));                                   // a relative entry could be anyone's folder
        Assert.Null(FfmpegRunner.LocateIn("." + Path.PathSeparator + ""));
        Assert.Equal(Path.Combine(real, "ffmpeg.exe"), FfmpegRunner.LocateIn(relative + Path.PathSeparator + real));
    }

    [Fact]
    public void Music_repeats_under_the_whole_video_dips_while_the_voice_speaks_and_ends_with_it()
    {
        var args = FfmpegArgs.Join("clips.txt", ["a0.wav", "a1.wav"], "out.mp4", "bed.mp3").ToList();
        var graph = args[args.IndexOf("-filter_complex") + 1];

        // The track is the input after the narration, and plays round and round.
        Assert.Equal(["-stream_loop", "-1", "-i", "bed.mp3"], args.Skip(args.IndexOf("bed.mp3") - 3).Take(4));
        Assert.Contains("[3:a]aformat=sample_rates=48000:channel_layouts=stereo,volume=0.25[bed]", graph);
        Assert.Contains("[a1]asplit[voice][key]", graph);
        Assert.Contains("[bed][key]sidechaincompress", graph);
        Assert.Contains("[voice][ducked]amix=inputs=2:duration=first", graph);
        Assert.Contains("[mixed]loudnorm", graph); // the mix is what is levelled, not the voice alone
        Assert.DoesNotContain("sidechaincompress", string.Join(' ', FfmpegArgs.Join("clips.txt", ["a0.wav"], "out.mp4")));
    }
}
