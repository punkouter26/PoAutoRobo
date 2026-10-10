namespace PoAutoRobo.Core.Tests;

public sealed class FilesTests
{
    [Fact]
    public void Sweeping_removes_what_a_crash_left_behind_and_leaves_recent_work_and_named_folders_alone()
    {
        // The app's real scratch folder, as the sweep has no other: everything made here is named for this test and removed by it.
        var old = Files.NewScratchFolder("sweep-test-old");
        var recent = Files.NewScratchFolder("sweep-test-recent");
        var kept = Directory.CreateDirectory(Path.Combine(Files.ScratchRoot, "sweep-test-kept")).FullName;
        var stray = Files.NewScratchFile(".sweep-test");
        try
        {
            File.WriteAllText(Path.Combine(old, "frame.jpg"), "x");
            File.WriteAllText(stray, "x");
            foreach (var path in new[] { old, kept })
                Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
            File.SetLastWriteTimeUtc(stray, DateTime.UtcNow.AddDays(-3));

            Files.SweepScratch(TimeSpan.FromDays(1), "sweep-test-kept");

            Assert.False(Directory.Exists(old));
            Assert.False(File.Exists(stray));
            Assert.True(Directory.Exists(recent));
            Assert.True(Directory.Exists(kept));
        }
        finally
        {
            foreach (var folder in new[] { old, recent, kept }.Where(Directory.Exists))
                Directory.Delete(folder, recursive: true);
            File.Delete(stray);
        }
    }
}
