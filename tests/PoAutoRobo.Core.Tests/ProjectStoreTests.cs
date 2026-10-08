using System.Text.Json;

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

    private static Episode Change(Episode episode, int clip, Func<Clip, Clip> change) => EpisodeEditor.Update(episode, episode.Clips[clip].Id, change);

    [Fact]
    public void A_saved_episode_reopens_with_everything_and_needs_no_services()
    {
        var episode = EpisodeEditor.SetMix(NewEpisode(4), new MixPercentages(100, 0, 0, 0)) with
        {
            Captions = new CaptionStyle(CaptionPreset.ComicBanner, 72, "#11AAFF", 6),
        };
        episode = EpisodeEditor.Reorder(episode, [.. episode.Clips.Reverse().Select(c => c.Id)]);
        episode = Change(episode, 0, c => c with { Visual = new VisualSpec(VisualKind.Still, MediaPaths: ["a.png"], Stale: true) });
        episode = EpisodeEditor.AttachVideo(episode, episode.Clips[1].Id, "lab.mp4", new FitResult("Fitted.", 1.05, TimeSpan.FromSeconds(9), TimeSpan.Zero, true));
        episode = EpisodeEditor.SetHostVisible(EpisodeEditor.SetTier(episode, episode.Clips[2].Id, Tier.C), episode.Clips[2].Id, false);
        // As a live script arrives: depth B only. The other depths are written on demand, so their absence is not damage.
        episode = Change(episode, 3, c => c with { Scripts = new Dictionary<Tier, TierScript> { [Tier.B] = c.Scripts[Tier.B] } });
        ProjectStore.Save(episode, _folder);

        var reopened = ProjectStore.Load(_folder); // plain file read: no vault, no network

        Assert.Equal(Json(episode), Json(reopened));
        Assert.Equal(episode.Clips.Select(c => c.Id), reopened.Clips.Select(c => c.Id)); // the running order
        Assert.Equal(1.05, reopened.Clips[1].NarrationRate);
        Assert.True(reopened.Clips[0].Visual.Stale);
        Assert.Equal(Tier.B, Assert.Single(reopened.Clips[3].Scripts).Key);
    }

    [Fact]
    public void A_missing_or_corrupt_file_is_reported_and_the_backup_restores_the_previous_save()
    {
        Assert.Throws<InvalidDataException>(() => ProjectStore.Load(_folder)); // nothing saved yet

        var first = NewEpisode();
        ProjectStore.Save(first, _folder);
        ProjectStore.Save(first with { Title = "Second" }, _folder);
        File.WriteAllText(Path.Combine(_folder, "episode.json"), "{ not json");

        Assert.Throws<InvalidDataException>(() => ProjectStore.Load(_folder));
        Assert.Equal(first.Title, ProjectStore.RestoreBackup(_folder).Title);
        Assert.Equal(first.Title, ProjectStore.Load(_folder).Title);
    }

    // ---- An episode file can be edited by anyone who can reach the folder, so it is not trusted ----

    [Theory]
    [InlineData(@"\\attacker\share\a.png")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"..\..\outside.png")]
    public void Media_outside_the_episode_folder_is_dropped_on_load_and_media_inside_it_is_kept(string outside)
    {
        var inside = Path.Combine(_folder, "images", "abc.png");
        var episode = Change(NewEpisode(2), 0, c => c with { Visual = new VisualSpec(VisualKind.MultiPanel, MediaPaths: [outside, @"images\ok.png", inside]) });
        episode = Change(episode, 1, c => c with { NarrationRate = 1.05, Visual = new VisualSpec(VisualKind.UserVideo, KindLocked: true, UserVideoPath: outside) });
        ProjectStore.Save(episode, _folder);

        var loaded = ProjectStore.Load(_folder);

        Assert.Equal([@"images\ok.png", inside], loaded.Clips[0].Visual.MediaPaths); // relative or absolute, as long as it is inside
        // Footage that cannot be trusted turns the clip back into a title card at normal pace.
        Assert.Equal(new VisualSpec(VisualKind.TitleCard), loaded.Clips[1].Visual);
        Assert.Equal(1.0, loaded.Clips[1].NarrationRate);
    }

    [Fact]
    public void A_malformed_episode_file_is_reported_as_damaged_instead_of_crashing_later()
    {
        string[] damaged =
        [
            """{ "Title": "t", "Topic": "t", "Clips": null, "MixSeed": 1 }""",
            // The clip is on depth B but only depth A was saved.
            """{ "Title": "t", "Topic": "t", "MixSeed": 1, "Clips": [ { "Id": "3f2504e0-4f89-11d3-9a0c-0305e82c3301", "Title": "x", "ActiveTier": "B", "Scripts": { "A": { "Dialogue": "a", "VisualPrompt": "p", "Pose": "p" } }, "Visual": { "Kind": "Still" }, "HostVisible": true } ] }""",
            // The accent colour is written into the caption file, so it must be a colour and nothing else.
            """{ "Title": "t", "Topic": "t", "MixSeed": 1, "Clips": [], "Captions": { "Preset": "CleanSubtitle", "FontSize": 64, "AccentColor": "red; DROP", "StrokeWidth": 4 } }""",
        ];

        Assert.All(damaged, json =>
        {
            File.WriteAllText(Path.Combine(_folder, ProjectStore.FileName), json);

            Assert.Throws<InvalidDataException>(() => ProjectStore.Load(_folder));
        });
    }

    [Fact]
    public void Saved_source_passages_come_back_with_web_links_only_and_a_missing_or_corrupt_file_gives_none()
    {
        Assert.Empty(ProjectStore.LoadSnippets(_folder));

        ProjectStore.SaveSnippets(
        [
            new("unitreerobotics/unitree_rl_mjlab", "README.md", "https://github.com/unitreerobotics/unitree_rl_mjlab", "Supports R1."),
            new("a/b", "x.md", "search-ms:query=evil", "A link that would open something on this machine."),
            new("a/b", "y.md", @"\\attacker\share\y.md", "A link to a file share."),
        ], _folder);

        Assert.Equal("https://github.com/unitreerobotics/unitree_rl_mjlab", Assert.Single(ProjectStore.LoadSnippets(_folder)).Url);

        File.WriteAllText(Path.Combine(_folder, "grounding.json"), "{ not json");

        Assert.Empty(ProjectStore.LoadSnippets(_folder));
    }

    // ---- Episode folders ----

    [Fact]
    public void A_duplicate_gets_its_own_folder_and_title_points_at_its_own_media_and_leaves_the_finished_videos_behind()
    {
        var original = Path.Combine(_folder, "balance");
        foreach (var file in new[] { @"images\a.png", @"imports\lab.mp4", @"export\balance-1080p30.mp4" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(original, file))!);
            File.WriteAllText(Path.Combine(original, file), file);
        }
        var episode = Change(NewEpisode(2), 0, c => c with { Visual = new VisualSpec(VisualKind.Still, MediaPaths: [Path.Combine(original, @"images\a.png")]) });
        episode = Change(episode, 1, c => c with { Visual = new VisualSpec(VisualKind.UserVideo, KindLocked: true, UserVideoPath: @"imports\lab.mp4") });
        ProjectStore.Save(episode, original);

        var copy = ProjectStore.Duplicate(original, _folder);

        Assert.Equal(Path.Combine(_folder, "balance-copy"), copy);
        Assert.False(Directory.Exists(Path.Combine(copy, "export")));
        var loaded = ProjectStore.Load(copy);
        Assert.Equal("Balance copy", loaded.Title);
        Assert.Equal(episode.Clips.Select(c => c.Id), loaded.Clips.Select(c => c.Id));
        // Deleting the original must not take the copy's pictures with it.
        Assert.Equal([Path.Combine(copy, @"images\a.png")], loaded.Clips[0].Visual.MediaPaths);
        Assert.Equal(Path.Combine(copy, @"imports\lab.mp4"), loaded.Clips[1].Visual.UserVideoPath);
        Assert.True(File.Exists(loaded.Clips[0].Visual.MediaPaths![0]) && File.Exists(loaded.Clips[1].Visual.UserVideoPath));
        Assert.Equal(Json(episode), Json(ProjectStore.Load(original))); // the original is untouched
    }

    [Fact]
    public void Each_episode_gets_its_own_safely_named_folder_and_the_library_lists_them_newest_first()
    {
        var first = ProjectStore.NewFolder(_folder, "Balancing the R1");
        ProjectStore.Save(NewEpisode(1), first);
        var second = ProjectStore.NewFolder(_folder, "Balancing the R1");
        ProjectStore.Save(NewEpisode(1), second);
        File.SetLastWriteTimeUtc(Path.Combine(first, ProjectStore.FileName), DateTime.UtcNow.AddDays(-2));

        Assert.Equal(Path.Combine(_folder, "balancing-the-r1"), first);
        Assert.Equal(Path.Combine(_folder, "balancing-the-r1-2"), second);
        Assert.Equal(Path.Combine(_folder, "episode"), ProjectStore.NewFolder(_folder, "???")); // nothing usable in the title
        var pasted = ProjectStore.NewFolder(_folder, new string('a', 400) + " with a long summary pasted in");
        Assert.InRange(Path.GetFileName(pasted).Length, 1, 60);
        Directory.CreateDirectory(pasted); // must not throw; and with no episode file in it, it is not listed

        Assert.Equal([second, first], ProjectStore.ListEpisodes(_folder));
        Assert.Empty(ProjectStore.ListEpisodes(Path.Combine(_folder, "missing")));
    }
}
