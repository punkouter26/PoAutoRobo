using System.Collections.Concurrent;
using System.Numerics;
using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Render;
using Windows.Storage;

namespace PoAutoRobo.App;

// New cues go on the end; nothing saves them, but the order reads as the order they were added in.
public enum Cue { Finished, Stopped, Tick, Error, Delete, Undo, Sour, Land, Pour, Fanfare }

/// <summary>
/// The app's own sounds, worked out as numbers: short notes with a quick start and a fading tail, and a little noise.
/// Nothing is shipped as a file. Each sound is placed in the room in front of the listener, so on headphones a run of
/// ticks is heard travelling across; where that cannot be done it is set between the two speakers instead.
/// </summary>
public static class Sounds
{
    private const int Rate = 48000; // what placing a sound in a room asks for

    public static bool On { get; set; } = true;

    /// <summary>0 to 1.</summary>
    public static double Volume { get; set; } = 0.6;

    // A note: its pitch in hertz, when it starts and how long it rings in seconds, and how loud it is beside the others.
    private readonly record struct Note(double Pitch, double Start, double Length, double Loudness = 1);

    // A five-note scale with no note that clashes with another, so any run of steps makes a tune.
    private static readonly int[] Pentatonic = [0, 2, 4, 7, 9];

    /// <summary>The note <paramref name="step"/> places up the scale from <paramref name="root"/>, climbing two octaves and then starting again.</summary>
    private static double Step(double root, int step) => root * Math.Pow(2, (Pentatonic[step % 5] + 12 * (step / 5 % 2)) / 12.0);

    private static Note[] Score(Cue cue, int step) => cue switch
    {
        Cue.Finished => InTurn((523.25, 0.09), (659.25, 0.09), (783.99, 0.22)), // a rising major chord, one note at a time
        Cue.Stopped => InTurn((392.00, 0.12), (311.13, 0.22)),                  // two notes falling
        Cue.Error => InTurn((207.65, 0.13), (196.00, 0.30)),                    // two low notes a semitone apart: something is wrong
        Cue.Delete => InTurn((523.25, 0.05), (392.00, 0.05), (261.63, 0.14)),   // dropping away, quickly
        Cue.Undo => InTurn((659.25, 0.05), (523.25, 0.09)),                     // a small step back
        // One clip of a batch that did not come out: its note, with the one a semitone under it, together.
        Cue.Sour => [new(Step(1046.50, step), 0, 0.2, 0.7), new(Step(1046.50, step) * 0.944, 0, 0.22, 0.7)],
        // A placeholder taking its clip's title: a soft low note under its own, a step up the scale for each clip.
        Cue.Land => [new(Step(261.63, step), 0, 0.4, 0.8), new(Step(523.25, step), 0.01, 0.28, 0.35)],
        // The master video is finished: the chord again, all at once and held, with the octave ringing out above.
        Cue.Fanfare =>
        [
            new(261.63, 0, 1.4, 0.6), new(329.63, 0.02, 1.4, 0.5), new(392.00, 0.04, 1.4, 0.5), new(523.25, 0.06, 1.6, 0.55),
            new(1046.50, 0.30, 0.5, 0.25), new(1318.51, 0.42, 0.5, 0.22), new(1567.98, 0.54, 0.7, 0.22), new(2093.00, 0.66, 1.0, 0.2),
        ],
        _ => [new(Step(1046.50, step), 0, 0.05)], // one short high tick, a step up the scale for each clip of a batch
    };

    // Notes played one after another.
    private static Note[] InTurn(params (double Pitch, double Length)[] notes)
    {
        var start = 0.0;
        return [.. notes.Select(n => { var note = new Note(n.Pitch, start, n.Length); start += n.Length; return note; })];
    }

    /// <param name="pan">Where the sound sits from left to right: -1 left, 0 middle, 1 right.</param>
    /// <param name="step">For a tick, a sour note or a landing, how many places up the scale it is, so a run of them is a tune.</param>
    public static void Play(Cue cue, double pan = 0, int step = 0)
    {
        if (!On || Volume <= 0) return;
        _ = PlayAsync(cue, Math.Clamp(pan, -1, 1), Math.Max(0, step) % 10);
    }

    private static async Task PlayAsync(Cue cue, double pan, int step)
    {
        try
        {
            var (graph, speakers) = await Room.Value;
            if (graph is null || speakers is null)
            {
                PlayBetweenSpeakers(cue, pan, step);
                return;
            }
            // In front of the listener and a little way off, a metre and a half to either side at the most.
            var emitter = new AudioNodeEmitter(AudioNodeEmitterShape.CreateOmnidirectional(), AudioNodeEmitterDecayModel.CreateNatural(0.1, 1, 0.5, 50), AudioNodeEmitterSettings.None)
            {
                Position = new Vector3((float)pan * 1.5f, 0, -1.5f),
                SpatialAudioModel = SpatialAudioModel.ObjectBased,
            };
            var made = await graph.CreateFileInputNodeAsync(await StorageFile.GetFileFromPathAsync(FileFor(cue, step)), emitter);
            if (made.Status != AudioFileNodeCreationStatus.Success)
            {
                PlayBetweenSpeakers(cue, pan, step);
                return;
            }
            var node = made.FileInputNode;
            node.OutgoingGain = Volume;
            node.AddOutgoingConnection(speakers);
            // Let go of once it has played; not from inside the sound system's own call, which must not be held up.
            node.FileCompleted += (finished, _) => Task.Run(finished.Dispose);
        }
        catch (Exception)
        {
            // No sound device, a sound system that will not start: a missing chime must never be an error.
        }
    }

    // ---- In a room: one sound system for the app's life, started the first time a sound is wanted ----

    private static readonly Lazy<Task<(AudioGraph? Graph, AudioDeviceOutputNode? Speakers)>> Room = new(async () =>
    {
        try
        {
            var graph = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.SoundEffects));
            if (graph.Status != AudioGraphCreationStatus.Success) return (null, null);
            var speakers = await graph.Graph.CreateDeviceOutputNodeAsync();
            if (speakers.Status != AudioDeviceNodeCreationStatus.Success) return (null, null);
            graph.Graph.Start();
            return (graph.Graph, speakers.DeviceOutputNode);
        }
        catch (Exception)
        {
            return (null, null);
        }
    });

    /// <summary>The cue as a one-channel sound file, written the first time it is wanted. A sound is placed in a room from one channel.</summary>
    private static string FileFor(Cue cue, int step)
    {
        var path = Path.Combine(Files.ScratchRoot, "sounds", $"{cue}-{step}-{Rate}.wav");
        if (!File.Exists(path))
        {
            Files.EnsureFolderFor(path);
            File.WriteAllBytes(path, Wave(Samples(cue, step), 1));
        }
        return path;
    }

    // ---- Between the speakers: the fallback ----

    private static readonly MediaPlayer Player = new() { AudioCategory = MediaPlayerAudioCategory.SoundEffects };
    private static readonly ConcurrentDictionary<(Cue, int, int), byte[]> Made = new(); // each sound is worked out once for each place it is put

    private static void PlayBetweenSpeakers(Cue cue, double pan, int step)
    {
        var place = (int)Math.Round(pan * 10);
        var wave = Made.GetOrAdd((cue, step, place), _ =>
        {
            // Equal-power panning: the sound keeps its loudness as it moves across.
            var angle = (place / 10.0 + 1) * Math.PI / 4;
            return Wave(Samples(cue, step), Math.Cos(angle), Math.Sin(angle));
        });
        Player.Volume = Volume;
        Player.Source = MediaSource.CreateFromStream(new MemoryStream(wave).AsRandomAccessStream(), "audio/wav");
        Player.Play();
    }

    // ---- Working out the sound ----

    /// <summary>The cue as one channel of samples, -1 to 1.</summary>
    private static float[] Samples(Cue cue, int step)
    {
        if (cue == Cue.Pour) return Pouring();
        var notes = Score(cue, step);
        var samples = new float[(int)(notes.Max(n => n.Start + n.Length) * Rate)];
        foreach (var (pitch, start, length, loudness) in notes)
        {
            var first = (int)(start * Rate);
            for (var i = 0; i < (int)(length * Rate) && first + i < samples.Length; i++)
            {
                var t = (double)i / Rate;
                // A few milliseconds to come up, so the note does not click, then a fade over its whole length.
                var swell = Math.Min(1, t / 0.004) * Math.Exp(-4 * t / length);
                // The octave above, quietly, takes the edge off a bare tone.
                samples[first + i] += (float)((Math.Sin(2 * Math.PI * pitch * t) + 0.3 * Math.Sin(4 * Math.PI * pitch * t)) / 1.3 * swell * loudness * 0.8);
            }
        }
        // A chord adds up past full scale; it is brought back under it.
        var peak = samples.Length == 0 ? 0 : samples.Max(Math.Abs);
        if (peak > 0.9f)
            for (var i = 0; i < samples.Length; i++)
                samples[i] *= 0.9f / peak;
        return samples;
    }

    /// <summary>
    /// Liquid poured into a glass: noise through a filter whose opening wobbles, which the ear hears as bubbles, rising
    /// in pitch as the glass fills. Always the same, as the noise is drawn from the same seed.
    /// </summary>
    private static float[] Pouring()
    {
        const double Length = 0.8;
        var samples = new float[(int)(Length * Rate)];
        var random = new Random(3);
        var (low, band) = (0.0, 0.0);
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / Rate;
            var centre = (500 + 700 * t / Length) * (1 + 0.35 * Math.Sin(2 * Math.PI * 23 * t) * Math.Sin(2 * Math.PI * 3.1 * t));
            // A two-pole filter (state variable) gives the noise the hollow ring of the glass.
            var f = 2 * Math.Sin(Math.PI * centre / Rate);
            low += f * band;
            band += f * (random.NextDouble() * 2 - 1 - low - band / 6);
            var swell = Math.Min(1, t / 0.05) * Math.Min(1, (Length - t) / 0.2);
            samples[i] = (float)(band * swell * 0.5);
        }
        return samples;
    }

    /// <summary>A complete 16-bit WAV file holding the samples, with one channel for each loudness given.</summary>
    private static byte[] Wave(float[] samples, params double[] channels)
    {
        using var stream = new MemoryStream();
        using var file = new BinaryWriter(stream);
        WavInfo.WriteHeader(file, Rate, channels.Length, samples.Length);
        foreach (var value in samples)
            foreach (var channel in channels)
                file.Write((short)(Math.Clamp(value * channel, -1, 1) * short.MaxValue));
        file.Flush();
        return stream.ToArray();
    }
}
