using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

/// <summary>The warning and recovery states from SPEC §12 that are not covered by a feature's own tests.</summary>
public sealed class ErrorStateTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string Words(int n) => string.Join(' ', Enumerable.Repeat("word", n));

    private static Episode EpisodeOf(params int[] wordsPerClip)
    {
        var episode = ProjectStoreTests.NewEpisode(wordsPerClip.Length);
        return episode with { Clips = [.. episode.Clips.Select((c, i) => EpisodeEditor.WithDialogue(c, Words(wordsPerClip[i])))] };
    }

    [Fact]
    public void Runtime_is_the_sum_of_the_active_tiers()
    {
        Assert.Equal(TimeSpan.FromSeconds(60 + 20), Durations.Total(EpisodeOf(165, 55)));
    }

    [Theory]
    [InlineData(494, true)]   // 2:59.6
    [InlineData(495, false)]  // 3:00.0
    public void An_episode_under_three_minutes_is_flagged_but_nothing_stops_the_export(int words, bool isShort)
    {
        Assert.Equal(isShort, Durations.IsShort(Durations.Total(EpisodeOf(165, 165, words - 330))));
    }

    [Theory]
    [InlineData(30, "Shorter than 15 seconds")]
    [InlineData(100, null)]
    [InlineData(200, "Longer than 60 seconds")]
    public void A_clip_whose_active_tier_is_out_of_range_gets_a_plain_warning(int words, string? warning)
    {
        Assert.Equal(warning, Durations.Warning(EpisodeOf(words).Clips[0]));
    }

    [Fact]
    public void Only_the_active_tier_decides_the_warning()
    {
        var clip = EpisodeOf(100).Clips[0]; // tier B is fine; tiers A and C still hold three-word test lines

        Assert.Null(Durations.Warning(clip));
        Assert.NotNull(Durations.Warning(clip with { ActiveTier = Tier.A }));
    }

    [Fact]
    public void A_saved_episode_reopens_with_everything_and_needs_no_services()
    {
        var episode = EpisodeEditor.SetMix(EpisodeOf(100, 100, 100), new MixPercentages(100, 0, 0, 0)) with
        {
            Captions = new CaptionStyle(CaptionPreset.ComicBanner, 72, "#11AAFF", 6),
        };
        episode = EpisodeEditor.AttachVideo(episode, episode.Clips[1].Id, "lab.mp4", new FitResult("Fitted.", 1.05, TimeSpan.FromSeconds(9), TimeSpan.Zero, true));
        episode = EpisodeEditor.SetVisual(episode, episode.Clips[0].Id, new VisualSpec(VisualKind.Still, MediaPaths: ["a.png"], Stale: true));
        ProjectStore.Save(episode, _folder);

        var reopened = ProjectStore.Load(_folder); // plain file read: no vault, no network

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(episode, ProjectStore.JsonOptions), System.Text.Json.JsonSerializer.Serialize(reopened, ProjectStore.JsonOptions));
        Assert.Equal(1.05, reopened.Clips[1].NarrationRate);
        Assert.True(reopened.Clips[0].Visual.Stale);
    }

    [Fact]
    public void An_episode_folder_is_recognised_by_its_file_and_listed_newest_first()
    {
        var older = Path.Combine(_folder, "older");
        var newer = Path.Combine(_folder, "newer");
        ProjectStore.Save(ProjectStoreTests.NewEpisode(1), older);
        ProjectStore.Save(ProjectStoreTests.NewEpisode(1), newer);
        File.SetLastWriteTimeUtc(Path.Combine(older, ProjectStore.FileName), DateTime.UtcNow.AddDays(-2));
        Directory.CreateDirectory(Path.Combine(_folder, "not-an-episode"));

        Assert.Equal([newer, older], ProjectStore.ListEpisodes(_folder));
        Assert.Empty(ProjectStore.ListEpisodes(Path.Combine(_folder, "missing")));
    }
}
