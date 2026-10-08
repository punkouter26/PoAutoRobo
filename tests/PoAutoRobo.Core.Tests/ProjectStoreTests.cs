using System.Text.Json;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

public sealed class ProjectStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    internal static Clip NewClip(string title) => new(
        Guid.NewGuid(), title, Tier.B,
        Enum.GetValues<Tier>().ToDictionary(t => t, t => new TierScript($"{title} dialogue {t}", $"{title} prompt {t}", "pointing at whiteboard")),
        new VisualSpec(VisualKind.Still), HostVisible: true);

    internal static Episode NewEpisode(int clips = 3) =>
        new("Balance", "Whole-body balancing on the R1", [.. Enumerable.Range(1, clips).Select(i => NewClip($"Clip {i}"))], MixSeed: 42);

    private static string Json(Episode e) => JsonSerializer.Serialize(e, ProjectStore.JsonOptions);

    [Fact]
    public void Save_then_load_round_trips_everything()
    {
        var episode = NewEpisode();

        ProjectStore.Save(episode, _folder);

        Assert.Equal(Json(episode), Json(ProjectStore.Load(_folder)));
    }

    [Fact]
    public void Reordered_clips_persist_in_the_new_order()
    {
        var episode = NewEpisode();
        var reordered = episode with { Clips = [episode.Clips[2], episode.Clips[0], episode.Clips[1]] };

        ProjectStore.Save(reordered, _folder);

        Assert.Equal(reordered.Clips.Select(c => c.Id), ProjectStore.Load(_folder).Clips.Select(c => c.Id));
    }

    [Fact]
    public void Corrupt_file_throws_and_backup_restores_the_previous_save()
    {
        var first = NewEpisode();
        ProjectStore.Save(first, _folder);
        ProjectStore.Save(first with { Title = "Second" }, _folder);
        File.WriteAllText(Path.Combine(_folder, "episode.json"), "{ not json");

        Assert.Throws<InvalidDataException>(() => ProjectStore.Load(_folder));
        Assert.Equal(first.Title, ProjectStore.RestoreBackup(_folder).Title);
        Assert.Equal(first.Title, ProjectStore.Load(_folder).Title);
    }

    [Fact]
    public void Missing_file_throws_invalid_data()
    {
        Assert.Throws<InvalidDataException>(() => ProjectStore.Load(_folder));
    }

    [Fact]
    public void New_clip_defaults_are_tier_b_and_active_script_follows_the_tier()
    {
        var clip = NewClip("x");

        Assert.Equal("x dialogue B", clip.Active.Dialogue);
        Assert.Equal("x dialogue C", (clip with { ActiveTier = Tier.C }).Active.Dialogue);
    }
}
