using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

public sealed class ScriptRulesTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static string Words(int n) => string.Join(' ', Enumerable.Repeat("word", n));

    [Fact]
    public async Task Mock_episode_has_16_clips_with_three_tiers_all_in_range_and_tier_b_active()
    {
        var episode = await new MockScriptWriter().WriteEpisodeAsync("Balancing the R1", [], EpisodeLength.Full, Ct);

        Assert.Equal(16, episode.Clips.Count);
        Assert.All(episode.Clips, c =>
        {
            Assert.Equal(Tier.B, c.ActiveTier);
            Assert.Equal(3, c.Scripts.Count);
            Assert.All(c.Scripts.Values, s => Assert.True(Durations.InRange(Durations.Estimate(s.Dialogue)), s.Dialogue));
        });
        Assert.Empty(Durations.OutOfRange(episode));
        Assert.Equal(16, episode.Clips.Select(c => c.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(1, 1, 1)]     // quick test: one clip, about half a minute
    [InlineData(5, 5, 5)]     // short
    [InlineData(15, 20, 16)]  // full
    public async Task Mock_episode_can_be_as_short_as_one_clip(int min, int max, int expected)
    {
        var episode = await new MockScriptWriter().WriteEpisodeAsync("Balancing the R1", [], new EpisodeLength(min, max), Ct);

        Assert.Equal(expected, episode.Clips.Count);
        Assert.Empty(Durations.OutOfRange(episode));
    }

    [Fact]
    public void The_three_lengths_on_offer_are_full_short_and_quick_test()
    {
        Assert.Equal((15, 20), (EpisodeLength.Full.MinClips, EpisodeLength.Full.MaxClips));
        Assert.Equal((5, 5), (EpisodeLength.Short.MinClips, EpisodeLength.Short.MaxClips));
        Assert.Equal((1, 1), (EpisodeLength.QuickTest.MinClips, EpisodeLength.QuickTest.MaxClips));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(6, 5)]
    [InlineData(1, 21)]
    public void A_length_must_be_between_one_and_twenty_clips(int min, int max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EpisodeLength(min, max));
    }

    [Fact]
    public async Task Mock_tiers_differ_in_dialogue_and_visual_prompt()
    {
        var clip = (await new MockScriptWriter().WriteEpisodeAsync("x", [], EpisodeLength.Full, Ct)).Clips[0];

        Assert.Equal(3, clip.Scripts.Values.Select(s => s.Dialogue).Distinct().Count());
        Assert.Equal(3, clip.Scripts.Values.Select(s => s.VisualPrompt).Distinct().Count());
    }

    [Theory]
    [InlineData(165, 60)]
    [InlineData(55, 20)]
    [InlineData(0, 0)]
    public void Estimate_uses_165_words_per_minute(int words, int seconds)
    {
        Assert.Equal(seconds, Durations.Estimate(Words(words)).TotalSeconds, precision: 3);
    }

    [Theory]
    [InlineData(41, false)]  // 14.9s
    [InlineData(42, true)]
    [InlineData(165, true)]
    [InlineData(166, false)] // 60.4s
    public void Range_is_15_to_60_seconds_inclusive(int words, bool inRange)
    {
        Assert.Equal(inRange, Durations.InRange(Durations.Estimate(Words(words))));
    }

    [Fact]
    public void Out_of_range_tiers_are_flagged_per_clip_and_tier()
    {
        var clip = ProjectStoreTests.NewClip("short"); // three-word dialogues
        var episode = new Episode("t", "t", [clip], 0);

        Assert.Equal(Enum.GetValues<Tier>().Select(t => (clip.Id, t)), Durations.OutOfRange(episode));
    }

    [Fact]
    public void Target_words_matches_footage_seconds()
    {
        Assert.Equal(55, Durations.TargetWords(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Mock_rewrite_hits_the_requested_word_count()
    {
        var writer = new MockScriptWriter();

        Assert.Equal(30, Durations.WordCount(await writer.RewriteToLengthAsync(Words(80), 30, Ct)));
        Assert.Equal(120, Durations.WordCount(await writer.RewriteToLengthAsync(Words(80), 120, Ct)));
    }

    [Fact]
    public async Task Mock_drift_check_ignores_phrasing_but_catches_a_new_subject()
    {
        var writer = new MockScriptWriter();
        const string original = "The robot is balancing on one foot while the controller corrects its ankle torque.";

        Assert.False(await writer.CoreChangedAsync(original, "The robot is balancing on one foot as the controller corrects ankle torque!", Ct));
        Assert.True(await writer.CoreChangedAsync(original, "Now we open the waist actuator housing and inspect the gearbox.", Ct));
    }
}
