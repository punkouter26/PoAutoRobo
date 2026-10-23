using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace PoAutoRobo.Core.Voice;

/// <summary>Writes down what is said in a recording, with when each word is said.</summary>
/// <param name="wavPath">Speech as a 16 kHz mono WAV file (<see cref="FfmpegArgs.SpeechSound"/>).</param>
public delegate Task<IReadOnlyList<WordTiming>> Hear(string wavPath, CancellationToken ct);

/// <summary>Speech to text from Azure AI Speech, on the same resource and sign-in as the voice.</summary>
public sealed class AzureListener(AppSettings settings)
{
    /// <summary>Told how many seconds of sound were listened to, for the spend log.</summary>
    public Action<string, int, decimal>? Used { get; set; }

    public async Task<IReadOnlyList<WordTiming>> HearAsync(string wavPath, CancellationToken ct)
    {
        var config = SpeechConfig.FromEndpoint(settings.Endpoint, settings.Credential);
        config.SpeechRecognitionLanguage = "en-US";
        config.RequestWordLevelTimestamps();
        config.OutputFormat = OutputFormat.Detailed;

        var words = new List<WordTiming>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var audio = AudioConfig.FromWavFileInput(wavPath);
        using var recognizer = new SpeechRecognizer(config, audio);
        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason != ResultReason.RecognizedSpeech || e.Result.Best().FirstOrDefault() is not { } best) return;
            var heard = best.Words.ToList();
            // The timed words come lower-case and unpunctuated. The sentence as it would be written has the same
            // words, so where the counts agree its spelling is used, which makes captions fit to read.
            var written = Durations.SplitWords(e.Result.Text);
            lock (words)
                words.AddRange(heard.Select((w, i) => new WordTiming(written.Length == heard.Count ? written[i] : w.Word, TimeSpan.FromTicks(w.Offset), TimeSpan.FromTicks(w.Duration))));
        };
        recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
                finished.TrySetException(new InvalidOperationException($"The speech service could not listen to this film. {e.ErrorDetails}"));
            else
                finished.TrySetResult(); // the end of the file
        };
        recognizer.SessionStopped += (_, _) => finished.TrySetResult();

        await recognizer.StartContinuousRecognitionAsync();
        try
        {
            using (ct.Register(() => finished.TrySetCanceled(ct)))
                await finished.Task;
        }
        finally
        {
            await recognizer.StopContinuousRecognitionAsync();
        }
        Used?.Invoke(SpendLog.HeardSeconds, (int)Math.Ceiling(WavInfo.Duration(wavPath).TotalSeconds), 0);
        lock (words)
            return [.. words.OrderBy(w => w.Start)];
    }
}
