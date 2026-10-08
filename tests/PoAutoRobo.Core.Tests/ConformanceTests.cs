using NSubstitute;
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

    [Fact]
    public async Task Dialogue_is_rewritten_until_the_narration_matches_the_footage_even_when_the_writer_always_runs_long()
    {
        // 20s of dialogue for 40s of footage, and 30% too many words every time: aiming by the measured result still closes in.
        var fit = await Conformance.FitAsync(Words(60), S(40), WriterOffBy(1.3), Speak, Ct);

        Assert.True(fit.WithinTolerance);
        Assert.InRange(fit.Duration.TotalSeconds, 39, 41);
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

    [Fact]
    public async Task Footage_outside_5_to_120_seconds_is_rejected_before_any_work()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Conformance.FitAsync(Words(60), S(4.9), WriterOffBy(1.0), Speak, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Conformance.FitAsync(Words(60), S(120.1), WriterOffBy(1.0), Speak, Ct));

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
        var settings = await AppSettings.LoadAsync(new KeyVaultSecretSource(KeyVaultSecretSource.DefaultVault, AppSettings.SignedInUser), Ct);
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
}
