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
        // Every clip but the last runs 0.5s long so the picture can dissolve into the next one.
        Assert.Equal([S(20.35), S(30.35), S(15)], slots.Select(s => s.VideoLength));
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
    public Task Master_dissolves_pictures_crossfades_and_normalises_audio_and_burns_captions() =>
        Verify(Lines(FfmpegArgs.Master(
            ["clip_00.mp4", "clip_01.mp4", "clip_02.mp4"], ["a0.wav", "a1.wav", "a2.wav"],
            FfmpegArgs.Timeline([S(20), S(30), S(15)]), "captions.ass", ExportPreset.Hd30, "out.mp4")));

    [Fact]
    public Task Master_with_one_clip_and_no_captions_has_no_joins() =>
        Verify(Lines(FfmpegArgs.Master(
            ["clip_00.mp4"], ["a0.wav"], FfmpegArgs.Timeline([S(20)]), null, ExportPreset.Hd60, "out.mp4")));

    [Theory]
    [InlineData(1920, 1080, 30)]
    [InlineData(3840, 2160, 60)]
    public void Master_encodes_h264_and_aac_at_the_preset_frame_rate(int width, int height, int fps)
    {
        var args = FfmpegArgs.Master(["v.mp4"], ["a.wav"], FfmpegArgs.Timeline([S(20)]), null, new ExportPreset(width, height, fps), "out.mp4");

        Assert.Contains("libx264", args);
        Assert.Contains("aac", args);
        Assert.Equal(fps.ToString(), args[args.ToList().IndexOf("-r") + 1]);
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
