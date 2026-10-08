
namespace PoAutoRobo.Core.Tests;

public sealed class VisualMixTests
{
    private static IEnumerable<VisualKind> Kinds(Episode e) => e.Clips.Select(c => c.Visual.Kind);

    [Theory]
    // still, panels, video, title, clips → expected counts
    [InlineData(25, 25, 25, 25, 15, 4, 4, 4, 3)]   // 3.75 each: three round up, ties go in listed order
    [InlineData(34, 33, 33, 0, 16, 6, 5, 5, 0)]    // 5.44, 5.28, 5.28: the one spare seat goes to the largest remainder
    public void Counts_use_largest_remainder_and_assignment_deals_exactly_those_counts(
        int still, int panels, int video, int title, int clips, int eStill, int ePanels, int eVideo, int eTitle)
    {
        var mix = new MixPercentages(still, panels, video, title);
        var episode = ProjectStoreTests.NewEpisode(clips);

        var counts = VisualMix.Counts(mix, clips);
        var assigned = VisualMix.Assign(episode, mix);

        Assert.Equal(
            (eStill, ePanels, eVideo, eTitle),
            (counts[VisualKind.Still], counts[VisualKind.MultiPanel], counts[VisualKind.AiVideo], counts[VisualKind.TitleCard]));
        Assert.All(counts, c => Assert.Equal(c.Value, Kinds(assigned).Count(k => k == c.Key)));
        Assert.Equal(episode.Clips.Select(c => c.Id), assigned.Clips.Select(c => c.Id)); // clips stay where they are
    }

    [Fact]
    public void Percentages_must_be_non_negative_and_sum_to_100()
    {
        Assert.Throws<ArgumentException>(() => VisualMix.Counts(new MixPercentages(99, 0, 0, 0), 16));
        Assert.Throws<ArgumentException>(() => VisualMix.Counts(new MixPercentages(110, -10, 0, 0), 16));
    }

    [Fact]
    public void Same_seed_gives_the_same_assignment_and_a_different_seed_reshuffles()
    {
        var episode = ProjectStoreTests.NewEpisode(20);
        var mix = new MixPercentages(25, 25, 25, 25);

        Assert.Equal(Kinds(VisualMix.Assign(episode, mix)), Kinds(VisualMix.Assign(episode, mix)));
        Assert.NotEqual(Kinds(VisualMix.Assign(episode, mix)), Kinds(VisualMix.Assign(episode with { MixSeed = 7 }, mix)));
    }

    [Fact]
    public void User_video_and_hand_picked_clips_are_left_alone_and_excluded_from_the_counts()
    {
        var episode = ProjectStoreTests.NewEpisode(12);
        var clips = episode.Clips.ToList();
        clips[0] = clips[0] with { Visual = new VisualSpec(VisualKind.UserVideo, UserVideoPath: "lab.mp4") };
        clips[1] = clips[1] with { Visual = new VisualSpec(VisualKind.AiVideo, KindLocked: true) };
        episode = episode with { Clips = clips };

        var assigned = VisualMix.Assign(episode, new MixPercentages(50, 0, 0, 50));

        Assert.Equal(clips[0].Visual, assigned.Clips[0].Visual);
        Assert.Equal(clips[1].Visual, assigned.Clips[1].Visual);
        Assert.Equal(5, Kinds(assigned).Count(k => k == VisualKind.Still)); // half of the ten that are left, not of all twelve
        Assert.Equal(5, Kinds(assigned).Count(k => k == VisualKind.TitleCard));
    }
}
