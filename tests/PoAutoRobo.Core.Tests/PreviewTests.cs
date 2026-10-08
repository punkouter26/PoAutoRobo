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
    public void Karaoke_cues_carry_the_line_and_which_word_is_lit()
    {
        var cues = AssCaptions.Cues(Segments, new CaptionStyle(CaptionPreset.KaraokeHighlight));

        Assert.Equal(7, cues.Count);
        Assert.Equal(["one", "two", "three", "four", "five"], cues[2].Words);
        Assert.Equal(2, cues[2].Highlight);
        Assert.Equal(TimeSpan.FromSeconds(10.8), cues[2].Start);
        Assert.Equal(["six", "seven"], cues[6].Words);
        Assert.Equal(1, cues[6].Highlight);
    }

    [Fact]
    public void Block_cues_have_no_highlight_and_split_into_two_lines_when_long()
    {
        var cues = AssCaptions.Cues(Segments, new CaptionStyle(CaptionPreset.TwoLineBlock));

        var cue = Assert.Single(cues);
        Assert.Equal(-1, cue.Highlight);
        Assert.Equal(4, cue.FirstLineWords);
        Assert.Equal(7, cue.Words.Count);
    }

    [Fact]
    public void The_cue_showing_at_a_moment_can_be_looked_up()
    {
        var cues = AssCaptions.Cues(Segments, new CaptionStyle(CaptionPreset.KaraokeHighlight));

        Assert.Null(AssCaptions.CueAt(cues, TimeSpan.FromSeconds(9.9)));
        Assert.Equal(1, AssCaptions.CueAt(cues, TimeSpan.FromSeconds(10.5))!.Highlight);
        Assert.Null(AssCaptions.CueAt(cues, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Caption_style_is_saved_with_the_episode_and_defaults_to_karaoke()
    {
        var episode = ProjectStoreTests.NewEpisode();
        Assert.Equal(new CaptionStyle(), episode.Captions);

        var styled = episode with { Captions = new CaptionStyle(CaptionPreset.ComicBanner, 72, "#11AAFF", 6) };
        ProjectStore.Save(styled, _folder);

        Assert.Equal(styled.Captions, ProjectStore.Load(_folder).Captions);
    }

    [FfmpegFact]
    public async Task Draft_preview_renders_without_burned_in_captions_and_reports_its_cues()
    {
        var episode = ProjectStoreTests.NewEpisode(2);
        var builder = new EpisodeBuilder(new MockNarrator(), new FfmpegRunner(FfmpegRunner.Locate()!));

        var preview = await builder.PreviewAsync(episode, _folder, null, default);

        Assert.True(File.Exists(preview.VideoPath));
        Assert.EndsWith("preview.mp4", preview.VideoPath);
        Assert.Equal(2, preview.Segments.Count);
        Assert.Equal(TimeSpan.Zero, preview.Segments[0].Offset);
        Assert.True(preview.Segments[1].Offset > TimeSpan.Zero);
    }
}
