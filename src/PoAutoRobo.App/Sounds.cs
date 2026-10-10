using System.Collections.Concurrent;
using System.Numerics;
using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Render;
using Windows.Storage;

namespace PoAutoRobo.App;

public enum Cue { Finished, Stopped, Tick, Error, Delete, Undo }

/// <summary>
/// The app's own sounds, worked out as numbers: short notes with a quick start and a fading tail. Nothing is shipped
/// as a file. Each sound is placed in the room in front of the listener, so on headphones a run of ticks is heard
/// travelling across; where that cannot be done it is set between the two speakers instead.
/// </summary>
public static class Sounds
{
    private const int Rate = 48000; // what placing a sound in a room asks for

    public static bool On { get; set; } = true;

    /// <summary>0 to 1.</summary>
    public static double Volume { get; set; } = 0.6;

    // Pitch in hertz and length in seconds, played one after another.
    private static (double Pitch, double Length)[] Notes(Cue cue) => cue switch
    {
        Cue.Finished => [(523.25, 0.09), (659.25, 0.09), (783.99, 0.22)], // a rising major chord, one note at a time
        Cue.Stopped => [(392.00, 0.12), (311.13, 0.22)],                  // two notes falling
        Cue.Error => [(207.65, 0.13), (196.00, 0.30)],                    // two low notes a semitone apart: something is wrong
        Cue.Delete => [(523.25, 0.05), (392.00, 0.05), (261.63, 0.14)],   // dropping away, quickly
        Cue.Undo => [(659.25, 0.05), (523.25, 0.09)],                     // a small step back
        _ => [(1046.50, 0.05)],                                           // one short high tick
    };

    /// <param name="pan">Where the sound sits from left to right: -1 left, 0 middle, 1 right.</param>
    public static void Play(Cue cue, double pan = 0)
    {
        if (!On || Volume <= 0) return;
        _ = PlayAsync(cue, Math.Clamp(pan, -1, 1));
    }

    private static async Task PlayAsync(Cue cue, double pan)
    {
        try
        {
            var (graph, speakers) = await Room.Value;
            if (graph is null || speakers is null)
            {
                PlayBetweenSpeakers(cue, pan);
                return;
            }
            // In front of the listener and a little way off, a metre and a half to either side at the most.
            var emitter = new AudioNodeEmitter(AudioNodeEmitterShape.CreateOmnidirectional(), AudioNodeEmitterDecayModel.CreateNatural(0.1, 1, 0.5, 50), AudioNodeEmitterSettings.None)
            {
                Position = new Vector3((float)pan * 1.5f, 0, -1.5f),
                SpatialAudioModel = SpatialAudioModel.ObjectBased,
            };
            var made = await graph.CreateFileInputNodeAsync(await StorageFile.GetFileFromPathAsync(FileFor(cue)), emitter);
            if (made.Status != AudioFileNodeCreationStatus.Success)
            {
                PlayBetweenSpeakers(cue, pan);
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
    private static string FileFor(Cue cue)
    {
        var path = Path.Combine(Files.ScratchRoot, "sounds", $"{cue}-{Rate}.wav");
        if (!File.Exists(path))
        {
            Files.EnsureFolderFor(path);
            File.WriteAllBytes(path, Wave(Notes(cue), 1));
        }
        return path;
    }

    // ---- Between the speakers: the fallback ----

    private static readonly MediaPlayer Player = new() { AudioCategory = MediaPlayerAudioCategory.SoundEffects };
    private static readonly ConcurrentDictionary<(Cue, int), byte[]> Made = new(); // each sound is worked out once for each place it is put

    private static void PlayBetweenSpeakers(Cue cue, double pan)
    {
        var place = (int)Math.Round(pan * 10);
        var wave = Made.GetOrAdd((cue, place), _ =>
        {
            // Equal-power panning: the sound keeps its loudness as it moves across.
            var angle = (place / 10.0 + 1) * Math.PI / 4;
            return Wave(Notes(cue), Math.Cos(angle), Math.Sin(angle));
        });
        Player.Volume = Volume;
        Player.Source = MediaSource.CreateFromStream(new MemoryStream(wave).AsRandomAccessStream(), "audio/wav");
        Player.Play();
    }

    /// <summary>A complete 16-bit WAV file holding the notes, with one channel for each loudness given.</summary>
    private static byte[] Wave((double Pitch, double Length)[] notes, params double[] channels)
    {
        var samples = notes.Sum(n => (int)(n.Length * Rate));
        var frame = channels.Length * 2; // bytes for one sample of every channel

        using var stream = new MemoryStream();
        using var file = new BinaryWriter(stream);
        file.Write("RIFF"u8);
        file.Write(36 + samples * frame);
        file.Write("WAVEfmt "u8);
        file.Write(16);                      // length of the format block
        file.Write((short)1);                // plain samples, not compressed
        file.Write((short)channels.Length);
        file.Write(Rate);
        file.Write(Rate * frame);            // bytes a second
        file.Write((short)frame);
        file.Write((short)16);               // bits in a sample
        file.Write("data"u8);
        file.Write(samples * frame);
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
                foreach (var channel in channels)
                    file.Write((short)(value * channel * short.MaxValue));
            }
        }
        file.Flush();
        return stream.ToArray();
    }
}
