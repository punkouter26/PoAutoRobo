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

    private static string Tag(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
}
