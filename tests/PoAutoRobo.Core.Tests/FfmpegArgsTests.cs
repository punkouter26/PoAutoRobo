using System.Globalization;

namespace PoAutoRobo.Core.Tests;

public sealed class FfmpegArgsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static string Lines(IEnumerable<string> args) => string.Join('\n', args);

    [Fact]
    public void The_timeline_follows_the_narration_and_an_upright_short_is_laid_out_for_its_narrow_frame()
    {
        Timeline_follows_the_narration_with_audio_overlapping_at_each_join();
        An_upright_short_sizes_its_title_by_width_and_wraps_its_captions_to_the_narrow_frame();
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
        Assert.EndsWith(clip[clip.IndexOf("-c:v", StringComparison.Ordinal)..clip.IndexOf("-y", StringComparison.Ordinal)], intro[..intro.IndexOf("-y", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.All(new[] { intro, outro }, card => Assert.Equal(card.Split("drawtext").Length - 1, card.Split("expansion=none").Length - 1));

        return Verify(
            $"--- intro ---\n{intro}\n" +
            $"--- intro, channel name only ---\n{Lines(FfmpegArgs.IntroVideo(bare, "intro_channel.txt", null, "intro_title.txt", S(5), ExportPreset.Hd30, "intro.mp4"))}\n" +
            $"--- outro ---\n{outro}\n" +
            $"--- outro, channel name only ---\n{Lines(FfmpegArgs.OutroVideo(bare, "outro_channel.txt", null, S(8), ExportPreset.Hd30, "outro.mp4"))}");
    }

    [Fact]
    public Task Video_clip_is_fitted_and_its_last_frame_held() =>
        Verify(Lines(FfmpegArgs.ClipVideo(ClipSource.Video, "lab.mp4", S(12), ExportPreset.Uhd60, "clip_01.mp4")));

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

        return Verify(Lines(FfmpegArgs.Join("clips.txt", ["a0.wav", "a1.wav", "a2.wav"], "out.mp4")));
    }

    private void An_upright_short_sizes_its_title_by_width_and_wraps_its_captions_to_the_narrow_frame()
    {
        var card = Lines(FfmpegArgs.ClipVideo(ClipSource.TitleCard, "title_00.txt", S(15), ExportPreset.Shorts, "clip_00.mp4"));
        CaptionSegment[] words = [new(S(0), [new WordTiming("Hello.", S(0), S(0.4))])];

        Assert.Contains("s=1080x1920", card);
        Assert.Contains("fontsize=w/12", card); // sized by height, a title would run off the sides
        var upright = AssCaptions.Build(words, new CaptionStyle(), portrait: true);
        Assert.Contains("PlayResX: 1080", upright);
        Assert.Contains("PlayResY: 1920", upright);
        Assert.Contains("WrapStyle: 0", upright);
        Assert.Contains("PlayResX: 1920", AssCaptions.Build(words, new CaptionStyle())); // the master is unchanged
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
