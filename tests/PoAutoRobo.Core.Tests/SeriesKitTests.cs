namespace PoAutoRobo.Core.Tests;

public sealed class SeriesKitTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-series").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_brand_kit_keeps_its_own_copies_survives_a_restart_shrugs_off_damage_and_gives_new_episodes_its_look()
    {
        Assert.Equal(SeriesKit.Starter, SeriesKit.Load(_folder)); // nothing set up yet: the series this app is made for

        var picked = Path.Combine(Directory.CreateTempSubdirectory("poautorobo-picked").FullName, "My Logo.PNG");
        File.WriteAllText(picked, "png");
        var kit = new SeriesKit
        {
            ChannelName = "Teaching the R1",
            Tagline = "One skill at a time",
            Logo = SeriesKit.Import(_folder, picked, "logo"),
            Look = Look.Photoreal,
            Structure = BeatStructure.HookFirst,
            Captions = new CaptionStyle(CaptionPreset.CleanSubtitle, 70, "#22CC88", 3),
            OutroSeconds = 12,
        };
        kit.Save(_folder);
        Directory.Delete(Path.GetDirectoryName(picked)!, recursive: true); // the kit holds its own copy, not the file the user picked

        var reopened = SeriesKit.Load(_folder);
        Assert.Equal(kit, reopened);
        Assert.Equal("logo.png", reopened.Logo);
        Assert.Equal("png", File.ReadAllText(SeriesKit.PathOf(_folder, reopened.Logo)!));
        Assert.Null(SeriesKit.PathOf(_folder, reopened.Sting)); // none chosen
        Assert.Contains("\"Photoreal\"", File.ReadAllText(Path.Combine(_folder, SeriesKit.FileName))); // names, not numbers

        // Anyone can edit the file. What it names must be its own copies, and its numbers within reach.
        File.WriteAllText(Path.Combine(_folder, SeriesKit.FileName), """
            { "ChannelName": "Teaching the R1", "Logo": "..\\..\\secret.png", "Sting": "missing.wav", "OutroSeconds": 600, "Structure": 9, "Captions": { "AccentColor": "red" } }
            """);
        var edited = SeriesKit.Load(_folder);
        Assert.Null(SeriesKit.PathOf(_folder, edited.Logo));
        Assert.Null(SeriesKit.PathOf(_folder, edited.Sting));
        Assert.Equal(TimeSpan.FromSeconds(SeriesKit.MaxOutroSeconds), edited.Outro);
        Assert.Equal(new CaptionStyle(), edited.Captions);
        Assert.Equal(BeatStructure.Story, edited.Structure); // no such shape: the first
        File.WriteAllText(Path.Combine(_folder, SeriesKit.FileName), "{ not json");
        Assert.Equal(SeriesKit.Starter, SeriesKit.Load(_folder));

        // A new episode takes the series look; its clips and words are left alone, and no music means the episode keeps its own.
        var episode = ProjectStoreTests.NewEpisode(2) with { MusicPath = "imports\\old.mp3" };
        var styled = EpisodeEditor.UseSeriesLook(episode, kit, music: null);
        Assert.Equal((Look.Photoreal, kit.Captions, "imports\\old.mp3"), (styled.Look, styled.Captions, styled.MusicPath));
        Assert.Equal(episode.Clips.Select(c => c.Active), styled.Clips.Select(c => c.Active));
        Assert.Equal("imports\\bed.mp3", EpisodeEditor.UseSeriesLook(episode, kit, "imports\\bed.mp3").MusicPath);
        Assert.Same(styled, EpisodeEditor.UseSeriesLook(styled, kit, null)); // already in the series look: nothing to undo
    }
}
