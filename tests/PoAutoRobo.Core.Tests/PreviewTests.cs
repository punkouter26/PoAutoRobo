using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

public sealed class PreviewTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static CaptionSegment[] Segments =>
    [
        new(TimeSpan.FromSeconds(10), [.. "one two three four five six seven".Split(' ')
            .Select((w, i) => new WordTiming(w, TimeSpan.FromSeconds(0.4 * i), TimeSpan.FromSeconds(0.3)))]),
    ];

    [Fact]
    public void The_caption_showing_at_a_moment_can_be_looked_up_with_its_lit_word_and_line_break()
    {
        var karaoke = AssCaptions.Cues(Segments, new CaptionStyle(CaptionPreset.KaraokeHighlight));

        Assert.Null(AssCaptions.CueAt(karaoke, TimeSpan.FromSeconds(9.9)));
        var cue = AssCaptions.CueAt(karaoke, TimeSpan.FromSeconds(10.9))!;
        Assert.Equal(["one", "two", "three", "four", "five"], cue.Words);
        Assert.Equal(2, cue.Highlight);
        Assert.Null(AssCaptions.CueAt(karaoke, TimeSpan.FromSeconds(30)));

        // Block presets light no word, and a long caption is split over two lines.
        var block = Assert.Single(AssCaptions.Cues(Segments, new CaptionStyle(CaptionPreset.TwoLineBlock)));
        Assert.Equal(-1, block.Highlight);
        Assert.Equal(4, block.FirstLineWords);
        Assert.Equal(7, block.Words.Count);
    }

    [FfmpegFact]
    public async Task Draft_preview_renders_without_burned_in_captions_and_reports_its_cues_and_clip_marks()
    {
        var episode = ProjectStoreTests.NewEpisode(2);
        var builder = new EpisodeBuilder(new MockNarrator(), new FfmpegRunner(FfmpegRunner.Locate()!));

        var preview = await builder.PreviewAsync(episode, _folder, null, default);

        Assert.True(File.Exists(preview.VideoPath));
        Assert.EndsWith("preview.mp4", preview.VideoPath);
        Assert.Equal(2, preview.Segments.Count);
        Assert.Equal(TimeSpan.Zero, preview.Segments[0].Offset);
        Assert.True(preview.Segments[1].Offset > TimeSpan.Zero);
        // One mark per clip, where it starts, with the narration the waveform is drawn from.
        Assert.Equal(episode.Clips.Select(c => c.Title), preview.Marks.Select(m => m.Title));
        Assert.Equal(preview.Segments.Select(s => s.Offset), preview.Marks.Select(m => m.Start));
        Assert.All(preview.Marks, m => Assert.True(File.Exists(m.AudioPath)));
    }
}
