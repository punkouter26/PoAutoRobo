using PoAutoRobo.Core.Pipeline;

namespace PoAutoRobo.Core.Tests;

public sealed class FfmpegArgsTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static string Lines(IEnumerable<string> args) => string.Join('\n', args);

    [Fact]
    public void Timeline_follows_the_narration_with_audio_overlapping_at_each_join()
    {
        var slots = FfmpegArgs.Timeline([S(20), S(30), S(15)]);

        Assert.Equal([S(0), S(19.85), S(49.70)], slots.Select(s => s.Start));
        // Each picture runs exactly its share of the timeline, so finished clips can be joined without re-encoding.
        Assert.Equal([S(19.85), S(29.85), S(15)], slots.Select(s => s.VideoLength));
    }

    [Fact]
    public void Timeline_total_equals_the_crossfaded_narration_length()
    {
        var slots = FfmpegArgs.Timeline([S(20), S(30), S(15)]);

        Assert.Equal(S(65 - 2 * 0.15), slots[^1].Start + slots[^1].VideoLength);
    }

    [Fact]
    public Task Still_image_clip_gets_pan_and_zoom() =>
        Verify(Lines(FfmpegArgs.ClipVideo(ClipSource.Image, "panel.png", S(20.35), ExportPreset.Hd30, "clip_00.mp4")));

    [Fact]
    public Task Video_clip_is_fitted_and_its_last_frame_held() =>
        Verify(Lines(FfmpegArgs.ClipVideo(ClipSource.Video, "lab.mp4", S(12), ExportPreset.Uhd60, "clip_01.mp4")));

    [Fact]
    public Task Title_card_clip_is_drawn_from_a_text_file() =>
        Verify(Lines(FfmpegArgs.ClipVideo(ClipSource.TitleCard, "title_02.txt", S(15), ExportPreset.Hd30, "clip_02.mp4")));

    [Fact]
    public Task Captioned_clip_burns_its_own_captions_and_fades_in_and_out() =>
        Verify(Lines(FfmpegArgs.ClipVideo(ClipSource.Image, "panel.png", S(20), ExportPreset.Hd30, "clip_00.mp4", "captions_00.ass")));

    [Fact]
    public Task Join_copies_the_finished_pictures_and_crossfades_and_normalises_the_narration() =>
        Verify(Lines(FfmpegArgs.Join("clips.txt", ["a0.wav", "a1.wav", "a2.wav"], "out.mp4")));

    [Fact]
    public Task Join_with_one_clip_has_no_crossfade() =>
        Verify(Lines(FfmpegArgs.Join("clips.txt", ["a0.wav"], "out.mp4")));

    [Fact]
    public void Join_never_re_encodes_the_picture()
    {
        var args = FfmpegArgs.Join("clips.txt", ["a0.wav", "a1.wav"], "out.mp4").ToList();

        Assert.Equal("copy", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("concat", args[args.IndexOf("-f") + 1]);
        Assert.DoesNotContain("libx264", args);
        Assert.Contains("aac", args);
    }

    [Fact]
    public void Clip_list_names_each_file_in_order_with_quotes_made_safe()
    {
        Assert.Equal("file 'clip_00.mp4'\nfile 'it'\\''s.mp4'\n", FfmpegArgs.ClipList(["clip_00.mp4", "it's.mp4"]));
    }

    [Theory]
    [InlineData(ClipSource.Image)]
    [InlineData(ClipSource.Video)]
    [InlineData(ClipSource.TitleCard)]
    public void Every_kind_of_clip_is_encoded_identically_so_they_can_be_joined_by_copying(ClipSource source)
    {
        var args = string.Join(' ', FfmpegArgs.ClipVideo(source, "in", S(20), ExportPreset.Hd30, "o.mp4"));

        Assert.Contains("-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p -r 30 -video_track_timescale 90000", args);
        Assert.Contains("fade=t=in:st=0:d=0.250", args);
        Assert.Contains("fade=t=out:st=19.750:d=0.250", args);
    }

    [Theory]
    [InlineData(1920, 1080, 30)]
    [InlineData(3840, 2160, 60)]
    public void Clips_are_h264_at_the_preset_size_and_frame_rate(int width, int height, int fps)
    {
        var clip = FfmpegArgs.ClipVideo(ClipSource.Video, "v.mp4", S(20), new ExportPreset(width, height, fps), "o.mp4").ToList();

        Assert.Contains("libx264", clip);
        Assert.Equal(fps.ToString(), clip[clip.IndexOf("-r") + 1]);
        Assert.Contains($"scale={width}:{height}", string.Join(' ', clip));
    }

    [Fact]
    public void Numbers_use_a_dot_even_on_comma_decimal_systems()
    {
        var before = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Assert.Contains("20.350", FfmpegArgs.ClipVideo(ClipSource.Video, "a.mp4", S(20.35), ExportPreset.Hd30, "o.mp4"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = before;
        }
    }
}
