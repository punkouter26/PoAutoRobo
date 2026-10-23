using System.Text;

namespace PoAutoRobo.Core.Voice;

public static class WavInfo
{
    public static TimeSpan Duration(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        var length = reader.BaseStream.Length;
        if (length < 12 || Tag(reader) != "RIFF" || reader.ReadInt32() < 0 || Tag(reader) != "WAVE")
            throw new InvalidDataException($"{path} is not a WAV file.");

        var byteRate = 0;
        while (reader.BaseStream.Position + 8 <= length)
        {
            var id = Tag(reader);
            var size = reader.ReadUInt32();
            if (id == "fmt ")
            {
                reader.BaseStream.Seek(8, SeekOrigin.Current);
                byteRate = reader.ReadInt32();
                reader.BaseStream.Seek(size - 12, SeekOrigin.Current);
            }
            else if (id == "data" && byteRate > 0)
            {
                // Streamed writers can leave a wrong size here, so never trust it past the end of the file.
                var bytes = Math.Min(size, length - reader.BaseStream.Position);
                return TimeSpan.FromSeconds((double)bytes / byteRate);
            }
            else
            {
                reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }
        throw new InvalidDataException($"{path} has no audio data.");
    }

    /// <summary>
    /// The loudest sample in each of <paramref name="count"/> equal stretches of the file, 0 to 1, for drawing a waveform.
    /// Empty when the file is not 16-bit sound, which is all the narrators write.
    /// </summary>
    public static float[] Peaks(string path, int count)
    {
        var bytes = File.ReadAllBytes(path);
        // ponytail: looks for the first "data" tag instead of walking the chunks. A WAV with that word in an earlier
        // chunk would draw a wrong waveform; walk the chunks as Duration does if that ever happens.
        var data = bytes.AsSpan().IndexOf("data"u8);
        if (count <= 0 || bytes.Length < 44 || BitConverter.ToInt16(bytes, 34) != 16 || data < 0)
            return [];
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes.AsSpan(data + 8, (bytes.Length - data - 8) & ~1));
        var peaks = new float[count];
        for (var i = 0; i < samples.Length; i++)
        {
            var bucket = (int)((long)i * count / samples.Length);
            peaks[bucket] = Math.Max(peaks[bucket], Math.Abs(samples[i] / 32768f));
        }
        return peaks;
    }

    /// <summary>
    /// How strong the sound is in each of <paramref name="bands"/> bands of pitch, from 80 Hz to 8 kHz spaced as the ear
    /// hears them, <paramref name="perSecond"/> times a second: 0 for silence to 1 for full strength. For drawing a voice
    /// as it is heard. Empty when the file is not 16-bit sound.
    /// </summary>
    public static float[][] Spectrum(string path, int perSecond, int bands)
    {
        const int Window = 1024;
        var bytes = File.ReadAllBytes(path);
        var data = bytes.AsSpan().IndexOf("data"u8); // ponytail: as Peaks, and the format read from where a plain header has it
        if (bytes.Length < 44 || BitConverter.ToInt16(bytes, 34) != 16 || data < 0 || perSecond <= 0 || bands <= 0)
            return [];
        var (channels, rate) = (Math.Max((int)BitConverter.ToInt16(bytes, 22), 1), BitConverter.ToInt32(bytes, 24));
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes.AsSpan(data + 8, (bytes.Length - data - 8) & ~1));
        var count = samples.Length / channels;
        var frames = (int)((long)count * perSecond / Math.Max(rate, 1));
        var result = new float[frames][];
        var (real, imaginary) = (new double[Window], new double[Window]);
        for (var f = 0; f < frames; f++)
        {
            // A window of sound centred on this moment, its edges eased off so they add no false pitches.
            var centre = (long)f * rate / perSecond;
            for (var i = 0; i < Window; i++)
            {
                var at = centre - Window / 2 + i;
                var value = at >= 0 && at < count ? samples[(int)at * channels] / 32768.0 : 0;
                (real[i], imaginary[i]) = (value * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1))), 0);
            }
            Fourier(real, imaginary);
            var strengths = new float[bands];
            for (var b = 0; b < bands; b++)
            {
                var (low, high) = (80 * Math.Pow(100, (double)b / bands), 80 * Math.Pow(100, (double)(b + 1) / bands));
                var (first, last) = ((int)(low * Window / rate), Math.Max((int)(low * Window / rate) + 1, (int)(high * Window / rate)));
                var power = 0.0;
                for (var k = first; k < Math.Min(last, Window / 2); k++)
                    power = Math.Max(power, real[k] * real[k] + imaginary[k] * imaginary[k]);
                // In decibels, the last 60 of them below full strength mapped to 0 to 1.
                strengths[b] = (float)Math.Clamp((10 * Math.Log10(power + 1e-12) - 10 * Math.Log10(Window * Window / 16.0) + 60) / 60, 0, 1);
            }
            result[f] = strengths;
        }
        return result;
    }

    // The fast Fourier transform, in place: turns a stretch of sound into the strength and phase of each pitch in it.
    private static void Fourier(double[] real, double[] imaginary)
    {
        var n = real.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            for (var start = 0; start < n; start += length)
                for (var k = 0; k < length / 2; k++)
                {
                    var (c, s) = (Math.Cos(angle * k), Math.Sin(angle * k));
                    var (a, b) = (start + k, start + k + length / 2);
                    var (tr, ti) = (real[b] * c - imaginary[b] * s, real[b] * s + imaginary[b] * c);
                    (real[b], imaginary[b]) = (real[a] - tr, imaginary[a] - ti);
                    (real[a], imaginary[a]) = (real[a] + tr, imaginary[a] + ti);
                }
        }
    }

    /// <summary>The opening of a plain 16-bit WAV file; the caller writes the samples after it, every channel in turn.</summary>
    /// <param name="frames">How many samples each channel will have.</param>
    public static void WriteHeader(BinaryWriter file, int rate, int channels, int frames)
    {
        var frame = channels * 2; // bytes for one sample of every channel
        file.Write("RIFF"u8);
        file.Write(36 + frames * frame);
        file.Write("WAVEfmt "u8);
        file.Write(16);                // length of the format block
        file.Write((short)1);          // plain samples, not compressed
        file.Write((short)channels);
        file.Write(rate);
        file.Write(rate * frame);      // bytes a second
        file.Write((short)frame);
        file.Write((short)16);         // bits in a sample
        file.Write("data"u8);
        file.Write(frames * frame);
    }

    private static string Tag(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
}
