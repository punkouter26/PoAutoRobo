
namespace PoAutoRobo.Core.Tests;

public sealed class ScriptRulesTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static string Words(int n) => string.Join(' ', Enumerable.Repeat("word", n));

    private static Episode EpisodeOf(params int[] wordsPerClip)
    {
        var episode = ProjectStoreTests.NewEpisode(wordsPerClip.Length);
        return episode with { Clips = [.. episode.Clips.Select((c, i) => EpisodeEditor.WithDialogue(c, Words(wordsPerClip[i])))] };
    }

    [Fact]
    public async Task Mock_episode_has_the_length_asked_for_with_three_tiers_all_in_range_and_tier_b_active()
    {
        await Mock_episode_has_the_length_asked_for_with_three_tiers_all_in_range_and_tier_b_activeCase(1, 1, 1); // quick test: one clip, about half a minute
        await Mock_episode_has_the_length_asked_for_with_three_tiers_all_in_range_and_tier_b_activeCase(15, 20, 16); // full
    }

    private async Task Mock_episode_has_the_length_asked_for_with_three_tiers_all_in_range_and_tier_b_activeCase(int min, int max, int expected)
    {
        var episode = await new MockScriptWriter().WriteEpisodeAsync("Balancing the R1", Subject.UnitreeR1, [], new EpisodeLength(min, max), Ct);

        Assert.Equal(expected, episode.Clips.Count);
        Assert.Equal(expected, episode.Clips.Select(c => c.Id).Distinct().Count());
        Assert.All(episode.Clips, c =>
        {
            Assert.Equal(Tier.B, c.ActiveTier);
            Assert.Equal(3, c.Scripts.Values.Select(s => s.Dialogue).Distinct().Count());
            Assert.Equal(3, c.Scripts.Values.Select(s => s.VisualPrompt).Distinct().Count());
            Assert.All(c.Scripts.Values, s => Assert.Null(Durations.Warning(s.Dialogue)));
        });
    }

    [Fact]
    public void Clips_and_episodes_are_timed_from_their_words_and_flagged_when_too_short_or_too_long()
    {
        A_clip_is_timed_at_165_words_a_minute_and_flagged_in_plain_words_when_its_active_tier_runs_outside_15_to_60_seconds();
        An_episode_runs_for_the_sum_of_its_active_tiers_and_is_flagged_under_three_minutes();
    }

    private void A_clip_is_timed_at_165_words_a_minute_and_flagged_in_plain_words_when_its_active_tier_runs_outside_15_to_60_seconds()
    {
        A_clip_is_timed_at_165_words_a_minute_and_flagged_in_plain_words_when_its_active_tier_runs_outside_15_to_60_secondsCase(41, "Shorter than 15 seconds"); // 14.9s
        A_clip_is_timed_at_165_words_a_minute_and_flagged_in_plain_words_when_its_active_tier_runs_outside_15_to_60_secondsCase(165, null); // 60.0s: the range includes its ends
        A_clip_is_timed_at_165_words_a_minute_and_flagged_in_plain_words_when_its_active_tier_runs_outside_15_to_60_secondsCase(166, "Longer than 60 seconds"); // 60.4s
    }

    private void A_clip_is_timed_at_165_words_a_minute_and_flagged_in_plain_words_when_its_active_tier_runs_outside_15_to_60_secondsCase(int words, string? warning)
    {
        var clip = EpisodeOf(words).Clips[0]; // tiers A and C still hold three-word test lines; only the active tier counts

        Assert.Equal(words / 165.0 * 60, Durations.Estimate(clip.Active.Dialogue).TotalSeconds, precision: 3);
        Assert.Equal(warning, Durations.Warning(clip));
    }

    private void An_episode_runs_for_the_sum_of_its_active_tiers_and_is_flagged_under_three_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(60 + 20), Durations.Total(EpisodeOf(165, 55)));
        Assert.True(Durations.IsShort(Durations.Total(EpisodeOf(165, 165, 164))));   // 2:59.6
        Assert.False(Durations.IsShort(Durations.Total(EpisodeOf(165, 165, 165))));  // 3:00.0: flagged only, never blocked
    }

    [Fact]
    public async Task Mock_writer_rewrites_to_the_requested_word_count_and_its_drift_check_ignores_phrasing_but_catches_a_new_subject()
    {
        var writer = new MockScriptWriter();
        const string original = "The robot is balancing on one foot while the controller corrects its ankle torque.";

        Assert.Equal(30, Durations.WordCount(await writer.RewriteToLengthAsync(Words(80), 30, Ct)));
        Assert.Equal(120, Durations.WordCount(await writer.RewriteToLengthAsync(Words(80), 120, Ct)));
        Assert.False(await writer.CoreChangedAsync(original, "The robot is balancing on one foot as the controller corrects ankle torque!", Ct));
        Assert.True(await writer.CoreChangedAsync(original, "Now we open the waist actuator housing and inspect the gearbox.", Ct));
    }
}
