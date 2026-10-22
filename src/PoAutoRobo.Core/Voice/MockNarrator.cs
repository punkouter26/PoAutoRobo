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
        // Timings are spread by word length, not measured. Real word boundaries come from AzureNarrator.
        return new Narration(outputPath, duration, WordTiming.Spread(text, duration));
    }
}
