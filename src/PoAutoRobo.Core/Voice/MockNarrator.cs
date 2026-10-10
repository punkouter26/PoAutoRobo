using Windows.Media.SpeechSynthesis;

namespace PoAutoRobo.Core.Voice;

/// <summary>Offline stand-in using the voice built into Windows, for when Azure Speech is not configured.</summary>
public sealed class MockNarrator : INarrator
{
    public async Task<Narration> SynthesizeAsync(string text, string outputPath, double rate, CancellationToken ct)
    {
        using var synthesizer = new SpeechSynthesizer();
        synthesizer.Options.SpeakingRate = rate;
        using var speech = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(ct);

        Files.EnsureFolderFor(outputPath);
        await using (var file = File.Create(outputPath))
            await speech.AsStreamForRead().CopyToAsync(file, ct);

        var duration = WavInfo.Duration(outputPath);
        return new Narration(outputPath, duration, SpreadWords(text, duration));
    }

    // ponytail: timings are spread by word length, not measured. Real word boundaries come from AzureNarrator.
    private static List<WordTiming> SpreadWords(string text, TimeSpan duration)
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
