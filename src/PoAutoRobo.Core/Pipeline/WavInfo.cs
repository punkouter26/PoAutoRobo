using System.Text;

namespace PoAutoRobo.Core.Pipeline;

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

    private static string Tag(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
}
