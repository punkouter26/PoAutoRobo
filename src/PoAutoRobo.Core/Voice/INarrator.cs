namespace PoAutoRobo.Core.Voice;

public sealed record WordTiming(string Text, TimeSpan Start, TimeSpan Duration)
{
    /// <summary>
    /// Spreads the words of <paramref name="text"/> across <paramref name="duration"/>, each given time in proportion
    /// to its length. For sound where nothing says when each word was spoken.
    /// </summary>
    // ponytail: guessed from word length, not heard. Speech-to-text would give the real times (SPEC open question 1).
    public static IReadOnlyList<WordTiming> Spread(string text, TimeSpan duration)
    {
        var words = Durations.SplitWords(text);
        var totalLetters = (double)words.Sum(w => w.Length);
        var timings = new List<WordTiming>(words.Length);
        var lettersSoFar = 0;
        foreach (var word in words)
        {
            timings.Add(new WordTiming(word, duration * (lettersSoFar / totalLetters), duration * (word.Length / totalLetters)));
            lettersSoFar += word.Length;
        }
        return timings;
    }
}

public sealed record Narration(string AudioPath, TimeSpan Duration, IReadOnlyList<WordTiming> Words);

public interface INarrator
{
    /// <param name="rate">Speaking rate multiplier; 1.0 is the voice's normal pace.</param>
    Task<Narration> SynthesizeAsync(string text, string outputPath, double rate, CancellationToken ct);
}
