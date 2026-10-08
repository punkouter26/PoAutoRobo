namespace PoAutoRobo.Core.Services;

public sealed record WordTiming(string Text, TimeSpan Start, TimeSpan Duration);

public sealed record Narration(string AudioPath, TimeSpan Duration, IReadOnlyList<WordTiming> Words);

public interface INarrator
{
    /// <param name="rate">Speaking rate multiplier; 1.0 is the voice's normal pace.</param>
    Task<Narration> SynthesizeAsync(string text, string outputPath, double rate, CancellationToken ct);
}
