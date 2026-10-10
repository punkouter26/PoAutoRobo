using System.Security.Cryptography;
using System.Text;

namespace PoAutoRobo.Core.Library;

/// <summary>The few things every part of the app does with files: make room for one, name one by its content, and work in scratch space.</summary>
public static class Files
{
    /// <summary>
    /// Everything the app leaves in the temp folder lives under this one folder, so it can be cleared in one go.
    /// In the temp folder and never an episode folder: that is usually inside a synced Documents folder, where the
    /// sync client locks new files.
    /// </summary>
    public static string ScratchRoot { get; } = Path.Combine(Path.GetTempPath(), "PoAutoRobo");

    /// <summary>Makes sure the folder a file is about to be written into exists.</summary>
    public static void EnsureFolderFor(string filePath) => Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);

    public static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string ContentHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>A new empty folder for one job's working files. The job deletes it when it ends; <see cref="SweepScratch"/> catches the rest.</summary>
    public static string NewScratchFolder(string job) =>
        Directory.CreateDirectory(Path.Combine(ScratchRoot, $"{job}-{Guid.NewGuid():N}")).FullName;

    /// <summary>A path for one scratch file, not yet created.</summary>
    public static string NewScratchFile(string extension)
    {
        Directory.CreateDirectory(ScratchRoot);
        return Path.Combine(ScratchRoot, $"{Guid.NewGuid():N}{extension}");
    }

    /// <summary>
    /// Removes working files a crash or a locked file left behind: anything directly under <see cref="ScratchRoot"/>
    /// untouched for <paramref name="olderThan"/>. Folders named in <paramref name="keep"/> look after themselves.
    /// </summary>
    public static void SweepScratch(TimeSpan olderThan, params string[] keep)
    {
        var root = new DirectoryInfo(ScratchRoot);
        if (!root.Exists) return;
        foreach (var entry in root.EnumerateFileSystemInfos().Where(e => DateTime.UtcNow - e.LastWriteTimeUtc > olderThan && !keep.Contains(e.Name, StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                if (entry is DirectoryInfo folder) folder.Delete(recursive: true);
                else entry.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still in use by another copy of the app: it is caught next time.
            }
        }
    }
}
