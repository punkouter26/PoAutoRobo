using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Pipeline;

/// <param name="Gap">Footage length minus narration length; positive means the narration ends early.</param>
public sealed record FitResult(string Dialogue, double Rate, TimeSpan Duration, TimeSpan Gap, bool WithinTolerance);

/// <summary>Speaks <paramref name="text"/> at <paramref name="rate"/> and returns how long the audio runs.</summary>
public delegate Task<TimeSpan> Speak(string text, double rate, CancellationToken ct);

/// <summary>Makes a clip's narration last as long as the user's footage, by rewriting it and then nudging the pace.</summary>
public static class Conformance
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MinFootage = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxFootage = TimeSpan.FromSeconds(120);
    public const int MaxRewrites = 3;

    public static async Task<FitResult> FitAsync(string dialogue, TimeSpan footage, IScriptWriter writer, Speak speak, CancellationToken ct)
    {
        if (footage < MinFootage || footage > MaxFootage)
            throw new ArgumentOutOfRangeException(nameof(footage), "Footage must be between 5 seconds and 2 minutes long.");

        var best = (Text: dialogue, Duration: await speak(dialogue, 1.0, ct));
        var attempt = best;
        // Aim each request by how the last one actually came out, which also cancels a writer that runs long or short.
        var ask = (double)Durations.WordCount(dialogue);
        for (var i = 0; i < MaxRewrites && !Fits(best.Duration); i++)
        {
            ask *= footage / attempt.Duration;
            var text = await writer.RewriteToLengthAsync(attempt.Text, Math.Max(1, (int)Math.Round(ask)), ct);
            attempt = (text, await speak(text, 1.0, ct));
            if ((footage - attempt.Duration).Duration() < (footage - best.Duration).Duration())
                best = attempt;
        }
        if (Fits(best.Duration))
            return Result(best.Text, 1.0, best.Duration);

        var rate = Math.Clamp(best.Duration / footage, 1 - Ssml.MaxRateChange, 1 + Ssml.MaxRateChange);
        return Result(best.Text, rate, await speak(best.Text, rate, ct));

        bool Fits(TimeSpan duration) => (footage - duration).Duration() <= Tolerance;
        FitResult Result(string text, double r, TimeSpan duration) => new(text, r, duration, footage - duration, Fits(duration));
    }
}
