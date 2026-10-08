using Windows.Media.Core;
using Windows.Media.Playback;

namespace PoAutoRobo.App;

public enum Cue { Finished, Stopped, Tick }

/// <summary>
/// The app's own sounds, worked out as numbers each time one is played: short notes with a quick start and a
/// fading tail. Nothing is read from a file.
/// </summary>
public static class Sounds
{
    private const int Rate = 44100;

    private static readonly MediaPlayer Player = new() { AudioCategory = MediaPlayerAudioCategory.SoundEffects };

    public static bool On { get; set; } = true;

    /// <summary>0 to 1.</summary>
    public static double Volume { get; set; } = 0.6;

    // Pitch in hertz and length in seconds, played one after another.
    private static (double Pitch, double Length)[] Notes(Cue cue) => cue switch
    {
        Cue.Finished => [(523.25, 0.09), (659.25, 0.09), (783.99, 0.22)], // a rising major chord, one note at a time
        Cue.Stopped => [(392.00, 0.12), (311.13, 0.22)],                  // two notes falling
        _ => [(1046.50, 0.05)],                                           // one short high tick
    };

    /// <param name="pan">Where the sound sits between the speakers: -1 left, 0 middle, 1 right.</param>
    public static void Play(Cue cue, double pan = 0)
    {
        if (!On || Volume <= 0) return;
        Player.Volume = Volume;
        Player.Source = MediaSource.CreateFromStream(new MemoryStream(Wave(Notes(cue), pan)).AsRandomAccessStream(), "audio/wav");
        Player.Play();
    }

    /// <summary>A complete 16-bit stereo WAV file holding the notes.</summary>
    private static byte[] Wave((double Pitch, double Length)[] notes, double pan)
    {
        // Equal-power panning: the sound keeps its loudness as it moves across.
        var angle = (Math.Clamp(pan, -1, 1) + 1) * Math.PI / 4;
        var (left, right) = (Math.Cos(angle), Math.Sin(angle));
        var samples = notes.Sum(n => (int)(n.Length * Rate));

        using var stream = new MemoryStream();
        using var file = new BinaryWriter(stream);
        file.Write("RIFF"u8);
        file.Write(36 + samples * 4);
        file.Write("WAVEfmt "u8);
        file.Write(16);                // length of the format block
        file.Write((short)1);          // plain samples, not compressed
        file.Write((short)2);          // two channels
        file.Write(Rate);
        file.Write(Rate * 4);          // bytes a second
        file.Write((short)4);          // bytes for one sample of both channels
        file.Write((short)16);         // bits in a sample
        file.Write("data"u8);
        file.Write(samples * 4);
        foreach (var (pitch, length) in notes)
        {
            var count = (int)(length * Rate);
            for (var i = 0; i < count; i++)
            {
                var t = (double)i / Rate;
                // A few milliseconds to come up, so the note does not click, then a fade over its whole length.
                var loudness = Math.Min(1, t / 0.004) * Math.Exp(-4 * t / length);
                // The octave above, quietly, takes the edge off a bare tone.
                var value = (Math.Sin(2 * Math.PI * pitch * t) + 0.3 * Math.Sin(4 * Math.PI * pitch * t)) / 1.3 * loudness * 0.8;
                file.Write((short)(value * left * short.MaxValue));
                file.Write((short)(value * right * short.MaxValue));
            }
        }
        file.Flush();
        return stream.ToArray();
    }
}
