using System.Globalization;

namespace PoAutoRobo.Core.Tests;

public sealed class FfmpegArgsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static string Lines(IEnumerable<string> args) => string.Join('\n', args);

    [Fact]
    public void Timeline_follows_the_narration_with_audio_overlapping_at_each_join()
    {
        var slots = FfmpegArgs.Timeline([S(20), S(30), S(15)]);

        Assert.Equal([S(0), S(19.85), S(49.70)], slots.Select(s => s.Start));
        // Each picture runs exactly its share of the timeline, so finished clips can be joined without re-encoding.
        Assert.Equal([S(19.85), S(29.85), S(15)], slots.Select(s => s.VideoLength));
        Assert.Equal(S(65 - 2 * 0.15), slots[^1].Start + slots[^1].VideoLength); // the crossfaded narration length
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

    [Fact]
    public void An_upright_short_sizes_its_title_by_width_and_wraps_its_captions_to_the_narrow_frame()
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
}
