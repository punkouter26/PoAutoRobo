using System.Xml.Linq;

namespace PoAutoRobo.Core.Tests;

public sealed class SsmlTests : IDisposable
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
        var settings = await AppSettings.LoadAsync(new KeyVaultSecretSource(KeyVaultSecretSource.DefaultVault, AppSettings.SignedInUser), default);
        const string line = "Balance is hard, but I make tiny corrections every few milliseconds.";
        var path = Path.Combine(_folder, "line.wav");

        var narration = await new AzureNarrator(settings).SynthesizeAsync(line, path, 1.0, default);

        Assert.Equal(WavInfo.Duration(path), narration.Duration);
        Assert.InRange(narration.Duration.TotalSeconds, 2, 10);
        Assert.Equal(Durations.SplitWords(line), narration.Words.Select(w => w.Text));
        Assert.True(narration.Words[^1].Start + narration.Words[^1].Duration <= narration.Duration);
    }
}
