using System.Security.Cryptography;
using System.Text;

namespace PoAutoRobo.Core.Library;

/// <summary>
/// Generated media kept under a name derived from everything that went into the request. Asking again for the same
/// thing finds the file and makes no request, so an unchanged clip is never paid for twice.
/// </summary>
public sealed class MediaCache(string folder)
{
    /// <param name="keyParts">Everything that affects the result: model, size, prompt, reference content.</param>
    /// <param name="create">Writes the media to the path it is given. Only called when nothing is cached.</param>
    public async Task<string> GetOrCreateAsync(IEnumerable<string> keyParts, string extension, Func<string, Task> create)
    {
        var key = TextHash(string.Join('\u001f', keyParts))[..32];
        var path = Path.Combine(folder, key + extension);
        if (File.Exists(path))
            return path;

        // Made in the temp folder and moved in when complete, so a failure never leaves a half-written file to be reused.
        var scratch = Path.Combine(Path.GetTempPath(), $"poautorobo-{Guid.NewGuid():N}{extension}");
        try
        {
            await create(scratch);
            Directory.CreateDirectory(folder);
            File.Move(scratch, path, overwrite: true);
            return path;
        }
        finally
        {
            File.Delete(scratch);
        }
    }

    /// <summary>Makes sure the folder a file is about to be written into exists.</summary>
    public static void EnsureFolderFor(string filePath) => Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);

    public static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string ContentHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
