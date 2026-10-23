using System.Xml.Linq;

namespace PoAutoRobo.Core.Tests;

public sealed class VoiceTests : IDisposable
{
    private static readonly XNamespace Speak = "http://www.w3.org/2001/10/synthesis";
    private static readonly XNamespace Mstts = "http://www.w3.org/2001/mstts";
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_voice_document_is_well_formed_and_spoken_punctuation_joins_the_word_before_it()
    {
        The_voice_document_names_voice_style_and_rate_and_dialogue_cannot_add_markup_to_it();
        Punctuation_marks_join_the_word_before_them();
    }

    private void The_voice_document_names_voice_style_and_rate_and_dialogue_cannot_add_markup_to_it()
    {
        Document_names_the_voice_the_excited_style_and_a_rate_kept_within_ten_percent();
        Dialogue_cannot_inject_markup();
    }

    private void Document_names_the_voice_the_excited_style_and_a_rate_kept_within_ten_percent()
    {
        var doc = XDocument.Parse(Ssml.Build("Hello there.", "en-US-DavisNeural", 0.93));

        var voice = doc.Root!.Element(Speak + "voice")!;
        Assert.Equal("en-US-DavisNeural", voice.Attribute("name")!.Value);
        var style = voice.Element(Mstts + "express-as")!;
        Assert.Equal("excited", style.Attribute("style")!.Value);
        var prosody = style.Element(Speak + "prosody")!;
        Assert.Equal("-7%", prosody.Attribute("rate")!.Value);
        Assert.Equal("Hello there.", prosody.Value);
        // Further than ten percent either way starts to sound wrong, so the rate stops there.
        Assert.Contains("rate=\"+10%\"", Ssml.Build("x", "v", 1.5));
        Assert.Contains("rate=\"-10%\"", Ssml.Build("x", "v", 0.2));
    }

    private void Dialogue_cannot_inject_markup()
    {
        const string hostile = "Torque < 5 & rising </prosody><break time=\"10s\"/> \"quoted\"";

        var doc = XDocument.Parse(Ssml.Build(hostile, "en-US-DavisNeural", 1.0));

        Assert.Equal(hostile, doc.Descendants(Speak + "prosody").Single().Value);
        Assert.Empty(doc.Descendants(Speak + "break"));
    }

    private void Punctuation_marks_join_the_word_before_them()
    {
        (string Text, bool IsWord, double Start, double Length)[] events =
        [
            ("Balance", true, 0.0, 0.4), (",", false, 0.4, 0.1), ("then", true, 0.5, 0.2), ("walk", true, 0.7, 0.3), (".", false, 1.0, 0.1),
        ];

        var words = AzureNarrator.MergeBoundaries(events.Select(e => (e.Text, e.IsWord, TimeSpan.FromSeconds(e.Start), TimeSpan.FromSeconds(e.Length))));

        Assert.Equal(["Balance,", "then", "walk."], words.Select(w => w.Text));
        Assert.Equal(TimeSpan.FromSeconds(0.7), words[2].Start);
        Assert.Equal(TimeSpan.FromSeconds(0.3), words[2].Duration);
    }

    /// <summary>Opt-in: one short real synthesis.</summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_voice_returns_audio_and_a_timing_for_every_word()
    {
        if (Environment.GetEnvironmentVariable("POAUTOROBO_LIVE") != "1") return;
        var settings = await AppSettings.LoadAsync(KeyVault.Default(), default);
        const string line = "Balance is hard, but I make tiny corrections every few milliseconds.";
        var path = Path.Combine(_folder, "line.wav");

        var narration = await new AzureNarrator(settings).SynthesizeAsync(line, path, 1.0, default);

        Assert.Equal(WavInfo.Duration(path), narration.Duration);
        Assert.InRange(narration.Duration.TotalSeconds, 2, 10);
        Assert.Equal(Durations.SplitWords(line), narration.Words.Select(w => w.Text));
        Assert.True(narration.Words[^1].Start + narration.Words[^1].Duration <= narration.Duration);
    }

    // ---- Narration ----

    private const string Line = "The robot keeps its balance by making tiny corrections every few milliseconds.";
    /// <summary>Mono 16-bit PCM WAV of silence, with an extra chunk before the data so the reader must walk chunks.</summary>
    internal static void WriteSilentWav(string path, double seconds, int sampleRate = 16000)
    {
        var dataBytes = (int)(seconds * sampleRate) * 2;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + 12 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("LIST"u8); w.Write(4); w.Write("INFO"u8);
        w.Write("data"u8); w.Write(dataBytes); w.Write(new byte[dataBytes]);
    }

    [Fact]
    public async Task Audio_length_is_read_from_the_file_and_the_mock_narrator_writes_audio_that_agrees_with_its_timings()
    {
        Wav_duration_is_read_from_the_header_and_a_file_that_is_not_a_wav_is_rejected();
        await Mock_narrator_writes_audio_whose_length_and_word_timings_agree_and_a_faster_rate_shortens_it();
    }

    private void Wav_duration_is_read_from_the_header_and_a_file_that_is_not_a_wav_is_rejected()
    {
        var path = Path.Combine(_folder, "a.wav");
        WriteSilentWav(path, 12.5);

        Assert.Equal(12.5, WavInfo.Duration(path).TotalSeconds, precision: 3);

        File.WriteAllText(path, "this is not audio at all, just text");

        Assert.Throws<InvalidDataException>(() => WavInfo.Duration(path));
    }

    // The three below speak with the voice built into Windows, so they count as integration tests.

    private async Task Mock_narrator_writes_audio_whose_length_and_word_timings_agree_and_a_faster_rate_shortens_it()
    {
        await Mock_narrator_writes_a_wav_whose_duration_matches_the_file();
        await Mock_narrator_gives_one_timing_per_word_in_order_within_the_audio();
        await Faster_rate_gives_shorter_audio();
    }

    private async Task Mock_narrator_writes_a_wav_whose_duration_matches_the_file()
    {
        var path = Path.Combine(_folder, "line.wav");

        var narration = await new MockNarrator().SynthesizeAsync(Line, path, 1.0, CancellationToken.None);

        Assert.Equal(path, narration.AudioPath);
        Assert.True(narration.Duration > TimeSpan.FromSeconds(1));
        Assert.Equal(WavInfo.Duration(path), narration.Duration);
    }

    private async Task Mock_narrator_gives_one_timing_per_word_in_order_within_the_audio()
    {
        var narration = await new MockNarrator().SynthesizeAsync(Line, Path.Combine(_folder, "line.wav"), 1.0, CancellationToken.None);

        Assert.Equal(Durations.SplitWords(Line), narration.Words.Select(w => w.Text));
        Assert.Equal(TimeSpan.Zero, narration.Words[0].Start);
        Assert.All(narration.Words.Zip(narration.Words.Skip(1)), p => Assert.True(p.First.Start + p.First.Duration <= p.Second.Start + TimeSpan.FromTicks(1)));
        Assert.True(narration.Words[^1].Start + narration.Words[^1].Duration <= narration.Duration + TimeSpan.FromMilliseconds(1));
    }

    private async Task Faster_rate_gives_shorter_audio()
    {
        var narrator = new MockNarrator();

        var slow = await narrator.SynthesizeAsync(Line, Path.Combine(_folder, "slow.wav"), 0.9, CancellationToken.None);
        var fast = await narrator.SynthesizeAsync(Line, Path.Combine(_folder, "fast.wav"), 1.1, CancellationToken.None);

        Assert.True(fast.Duration < slow.Duration);
    }
}
