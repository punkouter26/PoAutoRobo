using NSubstitute;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

public sealed class ConformanceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private int _spoken;

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static string Words(int n) => string.Join(' ', Enumerable.Repeat("word", n));

    /// <summary>A voice that speaks exactly 3 words a second at normal rate.</summary>
    private Task<TimeSpan> Speak(string text, double rate, CancellationToken ct)
    {
        _spoken++;
        return Task.FromResult(S(Durations.WordCount(text) / 3.0 / rate));
    }

    /// <summary>A writer that returns <paramref name="factor"/> times the words it was asked for.</summary>
    private static IScriptWriter WriterOffBy(double factor)
    {
        var writer = Substitute.For<IScriptWriter>();
        writer.RewriteToLengthAsync(default!, default, default).ReturnsForAnyArgs(call => Words((int)Math.Round(call.ArgAt<int>(1) * factor)));
        return writer;
    }

    [Theory]
    [InlineData(40)]  // footage longer than the 20s of dialogue: expand
    [InlineData(8)]   // footage shorter: condense
    public async Task Dialogue_is_rewritten_until_the_narration_matches_the_footage(double footage)
    {
        var fit = await Conformance.FitAsync(Words(60), S(footage), WriterOffBy(1.0), Speak, Ct);

        Assert.True(fit.WithinTolerance);
        Assert.InRange(fit.Duration.TotalSeconds, footage - 1, footage + 1);
        Assert.Equal(1.0, fit.Rate);
        Assert.NotEqual(Words(60), fit.Dialogue);
    }

    [Fact]
    public async Task Narration_that_already_fits_is_left_alone()
    {
        var writer = WriterOffBy(1.0);

        var fit = await Conformance.FitAsync(Words(60), S(20.5), writer, Speak, Ct);

        Assert.Equal(Words(60), fit.Dialogue);
        Assert.True(fit.WithinTolerance);
        await writer.DidNotReceiveWithAnyArgs().RewriteToLengthAsync(default!, default, default);
    }

    [Fact]
    public async Task A_writer_that_always_runs_long_is_corrected_using_the_measured_pace()
    {
        // 30% too many words every time; aiming by the measured result still closes in.
        var fit = await Conformance.FitAsync(Words(60), S(40), WriterOffBy(1.3), Speak, Ct);

        Assert.InRange(fit.Duration.TotalSeconds, 39, 41);
    }

    [Fact]
    public async Task After_three_rewrites_the_speaking_rate_closes_the_remaining_gap()
    {
        // Always exactly 24 words = 8.0s, whatever is asked. Footage is 9.2s: rewrites cannot hit it, a slower pace can.
        var writer = Substitute.For<IScriptWriter>();
        writer.RewriteToLengthAsync(default!, default, default).ReturnsForAnyArgs(Words(24));

        var fit = await Conformance.FitAsync(Words(60), S(9.2), writer, Speak, Ct);

        await writer.ReceivedWithAnyArgs(Conformance.MaxRewrites).RewriteToLengthAsync(default!, default, default);
        Assert.True(fit.WithinTolerance);
        Assert.InRange(fit.Rate, 0.9, 1.1);
        Assert.NotEqual(1.0, fit.Rate);
    }

    [Fact]
    public async Task When_nothing_gets_close_the_closest_attempt_is_kept_and_the_gap_reported()
    {
        var writer = Substitute.For<IScriptWriter>();
        writer.RewriteToLengthAsync(default!, default, default).ReturnsForAnyArgs(Words(30)); // 10s, footage is 40s

        var fit = await Conformance.FitAsync(Words(60), S(40), writer, Speak, Ct);

        Assert.False(fit.WithinTolerance);
        Assert.Equal(Words(60), fit.Dialogue);       // 20s original is closer to 40s than the 10s rewrites
        Assert.Equal(0.9, fit.Rate, precision: 3);   // slowed as far as allowed
        Assert.Equal(S(40) - fit.Duration, fit.Gap);
    }

    [Theory]
    [InlineData(4.9)]
    [InlineData(120.1)]
    public async Task Footage_outside_5_to_120_seconds_is_rejected_before_any_work(double footage)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Conformance.FitAsync(Words(60), S(footage), WriterOffBy(1.0), Speak, Ct));

        Assert.Equal(0, _spoken);
    }

    /// <summary>Opt-in: the real writer and voice fitting a real line to longer and shorter footage (success criterion 7).</summary>
    [Theory]
    [Trait("Category", "Live")]
    [InlineData(34)]
    [InlineData(9)]
    public async Task Live_narration_lands_within_a_second_of_the_footage(double footage)
    {
        if (Environment.GetEnvironmentVariable("POAUTOROBO_LIVE") != "1") return;
        var settings = await AppSettings.LoadAsync(new KeyVaultSecretSource(KeyVaultSecretSource.DefaultVault), Ct);
        var narrator = new AzureNarrator(settings);
        var folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;
        const string line = "Next up, balance. I stay upright by reading my joint angles and my inertial sensor many times a second, then nudging my ankles, knees and hips before a wobble can grow. In simulation I practise that on thousands of slightly different floors, so the real lab floor feels like just one more of them.";
        try
        {
            var take = 0;
            var fit = await Conformance.FitAsync(line, S(footage), AzureScriptWriter.Create(settings),
                async (text, rate, ct) => (await narrator.SynthesizeAsync(text, Path.Combine(folder, $"take{take++}.wav"), rate, ct)).Duration, Ct);

            Assert.True(fit.WithinTolerance, $"footage {footage}s, narration {fit.Duration.TotalSeconds:0.00}s at rate {fit.Rate:0.00} after {take} takes");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Attaching_footage_sets_the_clip_to_my_video_with_the_fitted_words_and_rate()
    {
        var episode = ProjectStoreTests.NewEpisode(3);
        var fit = new FitResult("Fitted words.", 1.05, S(12), S(0.2), WithinTolerance: true);

        var edited = EpisodeEditor.AttachVideo(episode, episode.Clips[1].Id, @"imports\lab.mp4", fit);

        var clip = edited.Clips[1];
        Assert.Equal(VisualKind.UserVideo, clip.Visual.Kind);
        Assert.Equal(@"imports\lab.mp4", clip.Visual.UserVideoPath);
        Assert.Equal("Fitted words.", clip.Active.Dialogue);
        Assert.Equal(1.05, clip.NarrationRate);
        Assert.Same(episode.Clips[0], edited.Clips[0]);
    }

    [Fact]
    public void Removing_footage_returns_the_clip_to_a_generated_picture_at_normal_rate()
    {
        var episode = ProjectStoreTests.NewEpisode(2);
        var id = episode.Clips[0].Id;
        var withVideo = EpisodeEditor.AttachVideo(episode, id, "lab.mp4", new FitResult("Fitted.", 0.95, S(10), S(0), true));

        var clip = EpisodeEditor.RemoveVideo(withVideo, id).Clips[0];

        Assert.Equal(VisualKind.TitleCard, clip.Visual.Kind);
        Assert.Null(clip.Visual.UserVideoPath);
        Assert.Equal(1.0, clip.NarrationRate);
        Assert.Equal("Fitted.", clip.Active.Dialogue); // the words stay; only the picture and pace reset
    }

    [Fact]
    public void Narration_rate_is_saved_with_the_episode()
    {
        var folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;
        try
        {
            var episode = ProjectStoreTests.NewEpisode(1);
            ProjectStore.Save(EpisodeEditor.AttachVideo(episode, episode.Clips[0].Id, "lab.mp4", new FitResult("x", 1.07, S(9), S(0), true)), folder);

            Assert.Equal(1.07, ProjectStore.Load(folder).Clips[0].NarrationRate);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
