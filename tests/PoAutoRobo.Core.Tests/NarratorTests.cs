using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

public sealed class NarratorTests : IDisposable
{
    private const string Line = "The robot keeps its balance by making tiny corrections every few milliseconds.";
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

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

    [Theory]
    [InlineData(1.0)]
    [InlineData(12.5)]
    public void Wav_duration_is_read_from_the_header(double seconds)
    {
        var path = Path.Combine(_folder, "a.wav");
        WriteSilentWav(path, seconds);

        Assert.Equal(seconds, WavInfo.Duration(path).TotalSeconds, precision: 3);
    }

    [Fact]
    public void Non_wav_file_is_rejected()
    {
        var path = Path.Combine(_folder, "a.wav");
        File.WriteAllText(path, "this is not audio at all, just text");

        Assert.Throws<InvalidDataException>(() => WavInfo.Duration(path));
    }

    [Fact]
    public async Task Mock_narrator_writes_a_wav_whose_duration_matches_the_file()
    {
        var path = Path.Combine(_folder, "line.wav");

        var narration = await new MockNarrator().SynthesizeAsync(Line, path, 1.0, CancellationToken.None);

        Assert.Equal(path, narration.AudioPath);
        Assert.True(narration.Duration > TimeSpan.FromSeconds(1));
        Assert.Equal(WavInfo.Duration(path), narration.Duration);
    }

    [Fact]
    public async Task Mock_narrator_gives_one_timing_per_word_in_order_within_the_audio()
    {
        var narration = await new MockNarrator().SynthesizeAsync(Line, Path.Combine(_folder, "line.wav"), 1.0, CancellationToken.None);

        Assert.Equal(Durations.SplitWords(Line), narration.Words.Select(w => w.Text));
        Assert.Equal(TimeSpan.Zero, narration.Words[0].Start);
        Assert.All(narration.Words.Zip(narration.Words.Skip(1)), p => Assert.True(p.First.Start + p.First.Duration <= p.Second.Start + TimeSpan.FromTicks(1)));
        Assert.True(narration.Words[^1].Start + narration.Words[^1].Duration <= narration.Duration + TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Faster_rate_gives_shorter_audio()
    {
        var narrator = new MockNarrator();

        var slow = await narrator.SynthesizeAsync(Line, Path.Combine(_folder, "slow.wav"), 0.9, CancellationToken.None);
        var fast = await narrator.SynthesizeAsync(Line, Path.Combine(_folder, "fast.wav"), 1.1, CancellationToken.None);

        Assert.True(fast.Duration < slow.Duration);
    }
}
