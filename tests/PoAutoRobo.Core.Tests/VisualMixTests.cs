using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;

namespace PoAutoRobo.Core.Tests;

public sealed class VisualMixTests
{
    private static Dictionary<VisualKind, int> CountKinds(Episode e) =>
        e.Clips.GroupBy(c => c.Visual.Kind).ToDictionary(g => g.Key, g => g.Count());

    [Theory]
    // still, panels, video, title, clips → expected counts
    [InlineData(50, 20, 20, 10, 20, 10, 4, 4, 2)]
    [InlineData(100, 0, 0, 0, 17, 17, 0, 0, 0)]
    [InlineData(25, 25, 25, 25, 15, 4, 4, 4, 3)]   // 3.75 each: three round up, ties go in listed order
    [InlineData(34, 33, 33, 0, 16, 6, 5, 5, 0)]    // 5.44, 5.28, 5.28
    [InlineData(10, 10, 10, 70, 15, 2, 2, 1, 10)]  // 1.5, 1.5, 1.5, 10.5: two spare seats go to the first two
    public void Counts_use_largest_remainder_and_sum_to_the_clip_count(
        int still, int panels, int video, int title, int clips, int eStill, int ePanels, int eVideo, int eTitle)
    {
        var counts = VisualMix.Counts(new MixPercentages(still, panels, video, title), clips);

        Assert.Equal(clips, counts.Values.Sum());
        Assert.Equal(
            (eStill, ePanels, eVideo, eTitle),
            (counts[VisualKind.Still], counts[VisualKind.MultiPanel], counts[VisualKind.AiVideo], counts[VisualKind.TitleCard]));
    }

    [Theory]
    [InlineData(50, 50, 50, 50)]
    [InlineData(99, 0, 0, 0)]
    [InlineData(110, -10, 0, 0)]
    public void Percentages_must_be_non_negative_and_sum_to_100(int still, int panels, int video, int title)
    {
        Assert.Throws<ArgumentException>(() => VisualMix.Counts(new MixPercentages(still, panels, video, title), 16));
    }

    [Fact]
    public void Assignment_matches_the_counts()
    {
        var assigned = VisualMix.Assign(ProjectStoreTests.NewEpisode(20), new MixPercentages(50, 20, 20, 10));

        Assert.Equal(
            new Dictionary<VisualKind, int> { [VisualKind.Still] = 10, [VisualKind.MultiPanel] = 4, [VisualKind.AiVideo] = 4, [VisualKind.TitleCard] = 2 },
            CountKinds(assigned));
    }

    [Fact]
    public void Same_seed_gives_the_same_assignment_and_a_different_seed_reshuffles()
    {
        var episode = ProjectStoreTests.NewEpisode(20);
        var mix = new MixPercentages(25, 25, 25, 25);
        static IEnumerable<VisualKind> Kinds(Episode e) => e.Clips.Select(c => c.Visual.Kind);

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

        var assigned = VisualMix.Assign(episode, new MixPercentages(100, 0, 0, 0));

        Assert.Equal(clips[0].Visual, assigned.Clips[0].Visual);
        Assert.Equal(clips[1].Visual, assigned.Clips[1].Visual);
        Assert.All(assigned.Clips.Skip(2), c => Assert.Equal(VisualKind.Still, c.Visual.Kind));
    }

    [Fact]
    public void Assignment_keeps_clip_order_and_identity()
    {
        var episode = ProjectStoreTests.NewEpisode(16);

        var assigned = VisualMix.Assign(episode, new MixPercentages(25, 25, 25, 25));

        Assert.Equal(episode.Clips.Select(c => c.Id), assigned.Clips.Select(c => c.Id));
    }
}
