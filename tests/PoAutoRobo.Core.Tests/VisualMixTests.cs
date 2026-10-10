
namespace PoAutoRobo.Core.Tests;

public sealed class VisualMixTests
{
    private static IEnumerable<VisualKind> Kinds(Episode e) => e.Clips.Select(c => c.Visual.Kind);

    [Fact]
    public void Counts_use_largest_remainder_and_assignment_deals_exactly_those_counts()
    {
        // still, panels, title, clips → expected counts
        Counts_use_largest_remainder_and_assignment_deals_exactly_those_countsCase(40, 40, 20, 4, 2, 1, 1); // 1.6, 1.6, 0.8: two spare seats, the largest remainder first and then the earlier of the tied pair
        Counts_use_largest_remainder_and_assignment_deals_exactly_those_countsCase(34, 33, 33, 16, 6, 5, 5); // 5.44, 5.28, 5.28: the one spare seat goes to the largest remainder
    }

    private void Counts_use_largest_remainder_and_assignment_deals_exactly_those_countsCase(int still, int panels, int title, int clips, int eStill, int ePanels, int eTitle)
    {
        var mix = new MixPercentages(still, panels, title);
        var episode = ProjectStoreTests.NewEpisode(clips);

        var counts = VisualMix.Counts(mix, clips);
        var assigned = VisualMix.Assign(episode, mix);

        Assert.Equal((eStill, ePanels, eTitle), (counts[VisualKind.Still], counts[VisualKind.MultiPanel], counts[VisualKind.TitleCard]));
        Assert.All(counts, c => Assert.Equal(c.Value, Kinds(assigned).Count(k => k == c.Key)));
        Assert.Equal(episode.Clips.Select(c => c.Id), assigned.Clips.Select(c => c.Id)); // clips stay where they are
    }

    [Fact]
    public void A_mix_must_add_up_and_the_same_seed_always_deals_it_the_same_way()
    {
        Percentages_must_be_non_negative_and_sum_to_100();
        Same_seed_gives_the_same_assignment_and_a_different_seed_reshuffles();
    }

    private void Percentages_must_be_non_negative_and_sum_to_100()
    {
        Assert.Throws<ArgumentException>(() => VisualMix.Counts(new MixPercentages(99, 0, 0), 16));
        Assert.Throws<ArgumentException>(() => VisualMix.Counts(new MixPercentages(110, -10, 0), 16));
    }

    private void Same_seed_gives_the_same_assignment_and_a_different_seed_reshuffles()
    {
        var episode = ProjectStoreTests.NewEpisode(20);
        var mix = new MixPercentages(34, 33, 33);

        Assert.Equal(Kinds(VisualMix.Assign(episode, mix)), Kinds(VisualMix.Assign(episode, mix)));
        Assert.NotEqual(Kinds(VisualMix.Assign(episode, mix)), Kinds(VisualMix.Assign(episode with { MixSeed = 7 }, mix)));
    }

    [Fact]
    public void User_video_hand_picked_clips_and_the_kinds_the_script_chose_are_left_alone_and_excluded_from_the_counts()
    {
        var episode = ProjectStoreTests.NewEpisode(13);
        var clips = episode.Clips.ToList();
        clips[0] = clips[0] with { Visual = new VisualSpec(VisualKind.UserVideo, UserVideoPath: "lab.mp4") };
        clips[1] = clips[1] with { Visual = new VisualSpec(VisualKind.Still, KindLocked: true) };
        // Chosen by the script and already paid for: a re-deal of stills and title cards must not throw it away.
        clips[2] = clips[2] with { Visual = new VisualSpec(VisualKind.Animation, MediaPaths: ["scene.mp4"]) };
        episode = episode with { Clips = clips };

        var assigned = VisualMix.Assign(episode, new MixPercentages(50, 0, 50));

        Assert.Equal(clips[0].Visual, assigned.Clips[0].Visual);
        Assert.Equal(clips[1].Visual, assigned.Clips[1].Visual);
        Assert.Equal(clips[2].Visual, assigned.Clips[2].Visual);
        assigned = assigned with { Clips = [.. assigned.Clips.Skip(2)] }; // the hand-picked still is not one of the ten dealt
        Assert.Equal(5, Kinds(assigned).Count(k => k == VisualKind.Still)); // half of the ten that are left, not of all thirteen
        Assert.Equal(5, Kinds(assigned).Count(k => k == VisualKind.TitleCard));
    }
}
