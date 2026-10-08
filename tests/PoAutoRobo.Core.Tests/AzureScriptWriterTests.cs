using System.Text.Json;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

public sealed class AzureScriptWriterTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly AppSettings Settings = AppSettings.Offline;

    private sealed record Call(string Deployment, string System, string User, string SchemaName, string Schema);

    private readonly List<Call> _calls = [];
    private readonly Queue<string> _replies = new();

    private AzureScriptWriter Writer => new(Settings, (deployment, system, user, schemaName, schema, _) =>
    {
        _calls.Add(new Call(deployment, system, user, schemaName, schema));
        return Task.FromResult(_replies.Dequeue());
    });

    private static string EpisodeJson(int clips) => JsonSerializer.Serialize(new
    {
        title = "Balancing the R1",
        clips = Enumerable.Range(1, clips).Select(i => new
        {
            title = $"Clip {i}",
            a = new { dialogue = $"Simple line {i}.", visualPrompt = $"Analogy panel {i}", pose = "waving" },
            b = new { dialogue = $"Applied line {i}.", visualPrompt = $"Workflow panel {i}", pose = "pointing at a whiteboard" },
            c = new { dialogue = $"Advanced line {i}.", visualPrompt = $"Schematic panel {i}", pose = "holding a torque wrench" },
        }),
    });

    [Fact]
    public async Task Reply_becomes_an_episode_with_three_tiers_per_clip_starting_on_tier_b()
    {
        _replies.Enqueue(EpisodeJson(16));

        var episode = await Writer.WriteEpisodeAsync("Whole-body balance", [], EpisodeLength.Full, Ct);

        Assert.Equal("Balancing the R1", episode.Title);
        Assert.Equal("Whole-body balance", episode.Topic);
        Assert.Equal(16, episode.Clips.Count);
        var clip = episode.Clips[2];
        Assert.Equal("Clip 3", clip.Title);
        Assert.Equal(Tier.B, clip.ActiveTier);
        Assert.Equal(new TierScript("Simple line 3.", "Analogy panel 3", "waving"), clip.Scripts[Tier.A]);
        Assert.Equal(new TierScript("Advanced line 3.", "Schematic panel 3", "holding a torque wrench"), clip.Scripts[Tier.C]);
        Assert.True(clip.HostVisible);
        Assert.Equal(VisualKind.TitleCard, clip.Visual.Kind);
        Assert.Equal(16, episode.Clips.Select(c => c.Id).Distinct().Count());
        Assert.Equal(Settings.ChatDeployment, Assert.Single(_calls).Deployment);
    }

    [Fact]
    public async Task Wrong_clip_count_gets_one_retry_that_says_what_was_wrong()
    {
        _replies.Enqueue(EpisodeJson(12));
        _replies.Enqueue(EpisodeJson(17));

        var episode = await Writer.WriteEpisodeAsync("x", [], EpisodeLength.Full, Ct);

        Assert.Equal(17, episode.Clips.Count);
        Assert.Equal(2, _calls.Count);
        Assert.Contains("12 clips", _calls[1].User);
    }

    [Fact]
    public async Task Too_many_clips_twice_is_trimmed_to_twenty()
    {
        _replies.Enqueue(EpisodeJson(23));
        _replies.Enqueue(EpisodeJson(22));

        Assert.Equal(20, (await Writer.WriteEpisodeAsync("x", [], EpisodeLength.Full, Ct)).Clips.Count);
    }

    [Fact]
    public async Task Too_few_clips_twice_asks_the_user_to_try_again()
    {
        _replies.Enqueue(EpisodeJson(3));
        _replies.Enqueue(EpisodeJson(4));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Writer.WriteEpisodeAsync("x", [], EpisodeLength.Full, Ct));

        Assert.Contains("try again", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_quick_test_asks_for_exactly_one_clip_and_accepts_it()
    {
        _replies.Enqueue(EpisodeJson(1));

        var episode = await Writer.WriteEpisodeAsync("x", [], EpisodeLength.QuickTest, Ct);

        Assert.Single(episode.Clips);
        Assert.Single(_calls);
        Assert.Contains("exactly 1 clip", _calls[0].User);
        Assert.DoesNotContain("15", _calls[0].System);
    }

    [Fact]
    public async Task A_quick_test_that_comes_back_too_long_is_cut_to_one_clip()
    {
        _replies.Enqueue(EpisodeJson(4));
        _replies.Enqueue(EpisodeJson(3));

        Assert.Single((await Writer.WriteEpisodeAsync("x", [], EpisodeLength.QuickTest, Ct)).Clips);
    }

    [Fact]
    public async Task A_full_episode_asks_for_fifteen_to_twenty_clips()
    {
        _replies.Enqueue(EpisodeJson(16));

        await Writer.WriteEpisodeAsync("x", [], EpisodeLength.Full, Ct);

        Assert.Contains("between 15 and 20 clips", _calls[0].User);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"title\":\"t\"}")]
    public async Task Unusable_reply_is_reported_as_bad_data(string reply)
    {
        _replies.Enqueue(reply);
        _replies.Enqueue(reply);

        await Assert.ThrowsAsync<InvalidDataException>(() => Writer.WriteEpisodeAsync("x", [], EpisodeLength.Full, Ct));
    }

    [Fact]
    public async Task Grounding_snippets_reach_the_prompt_with_their_sources()
    {
        _replies.Enqueue(EpisodeJson(15));
        GroundingSnippet[] grounding = [new("unitreerobotics/unitree_rl_mjlab", "README.md", "https://github.com/unitreerobotics/unitree_rl_mjlab/blob/main/README.md", "Supports R1.")];

        await Writer.WriteEpisodeAsync("x", grounding, EpisodeLength.Full, Ct);

        Assert.Contains("https://github.com/unitreerobotics/unitree_rl_mjlab/blob/main/README.md", _calls[0].User);
        Assert.Contains("Supports R1.", _calls[0].User);
    }

    [Fact]
    public async Task System_prompt_and_schema_snapshot()
    {
        _replies.Enqueue(EpisodeJson(15));

        await Writer.WriteEpisodeAsync("x", [], EpisodeLength.Full, Ct);

        await Verify(_calls[0].System + "\n\n--- schema ---\n" + _calls[0].Schema);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Drift_check_uses_the_fast_model_and_returns_its_verdict(bool verdict)
    {
        _replies.Enqueue(JsonSerializer.Serialize(new { coreChanged = verdict }));

        Assert.Equal(verdict, await Writer.CoreChangedAsync("balancing on one foot", "inspecting the waist actuator", Ct));

        var call = Assert.Single(_calls);
        Assert.Equal(Settings.FastChatDeployment, call.Deployment);
        Assert.Contains("balancing on one foot", call.User);
        Assert.Contains("inspecting the waist actuator", call.User);
    }

    [Fact]
    public async Task Rewrite_asks_for_the_target_length_and_returns_the_new_dialogue()
    {
        _replies.Enqueue(JsonSerializer.Serialize(new { dialogue = "A longer version of the line." }));

        Assert.Equal("A longer version of the line.", await Writer.RewriteToLengthAsync("A line.", 90, Ct));

        Assert.Contains("90 words", Assert.Single(_calls).User);
    }

    /// <summary>Opt-in: one real call. Reports how well the model keeps to the rules instead of asserting on style.</summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_episode_has_15_to_20_clips_with_three_tiers()
    {
        if (Environment.GetEnvironmentVariable("POAUTOROBO_LIVE") != "1") return;
        var settings = await AppSettings.LoadAsync(new KeyVaultSecretSource(KeyVaultSecretSource.DefaultVault), Ct);

        var episode = await AzureScriptWriter.Create(settings).WriteEpisodeAsync("Training whole-body dynamic balancing on the Unitree R1 EDU", [], EpisodeLength.Full, Ct);

        Assert.InRange(episode.Clips.Count, 15, 20);
        Assert.All(episode.Clips, c => Assert.Equal(3, c.Scripts.Count));
        var outOfRange = Durations.OutOfRange(episode).Count();
        Assert.True(outOfRange <= episode.Clips.Count * 3 / 5, $"{outOfRange} of {episode.Clips.Count * 3} tier scripts fall outside 15–60s");
    }
}
