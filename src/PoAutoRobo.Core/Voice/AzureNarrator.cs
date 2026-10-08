using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace PoAutoRobo.Core.Voice;

/// <summary>The host's voice from Azure AI Speech, with a measured timing for every word.</summary>
public sealed class AzureNarrator(AppSettings settings) : INarrator
{
    /// <summary>Told what each recording used ("voice characters" and how many), for the episode's running cost.</summary>
    public Action<string, int>? Used { get; set; }

    public async Task<Narration> SynthesizeAsync(string text, string outputPath, double rate, CancellationToken ct)
    {
        MediaCache.EnsureFolderFor(outputPath);
        var config = SpeechConfig.FromEndpoint(settings.Endpoint, settings.Credential);
        config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff48Khz16BitMonoPcm);
        var boundaries = new List<(string Text, bool IsWord, TimeSpan Start, TimeSpan Length)>();

        // Both must be disposed before the file is read back, or it is still open and unfinished.
        using (var audio = AudioConfig.FromWavFileOutput(outputPath))
        using (var synthesizer = new SpeechSynthesizer(config, audio))
        {
            synthesizer.WordBoundary += (_, e) =>
                boundaries.Add((e.Text, e.BoundaryType == SpeechSynthesisBoundaryType.Word, TimeSpan.FromTicks((long)e.AudioOffset), e.Duration));
            using var stopOnCancel = ct.Register(() => _ = synthesizer.StopSpeakingAsync());

            using var result = await synthesizer.SpeakSsmlAsync(Ssml.Build(text, settings.Voice, rate));
            ct.ThrowIfCancellationRequested();
            if (result.Reason != ResultReason.SynthesizingAudioCompleted)
                throw new InvalidOperationException($"The voice service could not speak this line. {SpeechSynthesisCancellationDetails.FromResult(result).ErrorDetails}");
        }

        Used?.Invoke("voice characters", text.Length);
        return new Narration(outputPath, WavInfo.Duration(outputPath), MergeBoundaries(boundaries));
    }

    /// <summary>The service reports punctuation separately; captions want it attached to the word before it.</summary>
    internal static List<WordTiming> MergeBoundaries(IEnumerable<(string Text, bool IsWord, TimeSpan Start, TimeSpan Length)> boundaries)
    {
        var words = new List<WordTiming>();
        foreach (var (text, isWord, start, length) in boundaries)
        {
            if (isWord)
                words.Add(new WordTiming(text, start, length));
            else if (words.Count > 0)
                words[^1] = words[^1] with { Text = words[^1].Text + text };
        }
        return words;
    }
}
