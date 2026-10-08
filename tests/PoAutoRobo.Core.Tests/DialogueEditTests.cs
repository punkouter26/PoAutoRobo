using NSubstitute;

namespace PoAutoRobo.Core.Tests;

public sealed class DialogueEditTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;
    private readonly IScriptWriter _writer = Substitute.For<IScriptWriter>();
    private readonly Episode _episode;

    public DialogueEditTests()
    {
        var episode = ProjectStoreTests.NewEpisode(3);
        // Clip 1 already has a generated picture, so there is something that can go stale.
        var withPicture = episode.Clips[1] with { Visual = new VisualSpec(VisualKind.Still, MediaPaths: ["panel.png"]) };
        _episode = episode with { Clips = [episode.Clips[0], withPicture, episode.Clips[2]] };
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private Clip Target => _episode.Clips[1];

    [Theory]
    [InlineData(false)] // a phrasing change keeps the picture
    [InlineData(true)]  // a new action, tool or subject marks it out of date
    public async Task Edit_replaces_the_active_tier_for_that_clip_only_and_marks_the_picture_stale_only_when_the_subject_changed(bool coreChanged)
    {
        _writer.CoreChangedAsync(Target.Active.Dialogue, "New words.", Ct).Returns(coreChanged);

        var edited = await EpisodeEditor.EditDialogueAsync(_episode, Target.Id, "New words.", _writer, Ct);

        Assert.Equal("New words.", edited.Clips[1].Active.Dialogue);
        Assert.Equal(Target.Scripts[Tier.A], edited.Clips[1].Scripts[Tier.A]);
        Assert.Equal(Target.Scripts[Tier.C], edited.Clips[1].Scripts[Tier.C]);
        Assert.Same(_episode.Clips[0], edited.Clips[0]);
        Assert.Same(_episode.Clips[2], edited.Clips[2]);
        // The picture is never regenerated here, only flagged.
        Assert.Equal(Target.Visual with { Stale = coreChanged }, edited.Clips[1].Visual);
    }

    [Fact]
    public async Task Unchanged_or_whitespace_only_edit_does_nothing_and_asks_nobody_and_empty_dialogue_is_rejected()
    {
        var same = await EpisodeEditor.EditDialogueAsync(_episode, Target.Id, "  " + Target.Active.Dialogue + "\n", _writer, Ct);

        Assert.Same(_episode, same);
        // A touch-up that keeps nearly every word is plainly the same subject: the picture stays, and no paid question is asked.
        var line = "The robot keeps its balance by making tiny corrections many times every second without thinking.";
        var settled = EpisodeEditor.Update(_episode, Target.Id, c => EpisodeEditor.WithDialogue(c, line));
        var touched = await EpisodeEditor.EditDialogueAsync(settled, Target.Id, line.Replace("without thinking.", "without thinking, really."), _writer, Ct);
        Assert.False(touched.Clips[1].Visual.Stale);
        await _writer.DidNotReceiveWithAnyArgs().CoreChangedAsync(default!, default!, default);
        await Assert.ThrowsAsync<ArgumentException>(() => EpisodeEditor.EditDialogueAsync(_episode, Target.Id, "   ", _writer, Ct));
    }

    [Fact]
    public async Task Editing_one_clip_re_synthesises_only_that_clip()
    {
        var narrator = TestExtensions.SilentNarrator();
        var builder = new EpisodeBuilder(narrator, new FfmpegRunner("ffmpeg.exe"));
        await builder.NarrateAsync(_episode, _folder, Ct);
        narrator.ClearReceivedCalls();

        var edited = await EpisodeEditor.EditDialogueAsync(_episode, Target.Id, "A brand new line.", _writer, Ct);
        await builder.NarrateAsync(edited, _folder, Ct);

        await narrator.Received(1).SynthesizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await narrator.Received(1).SynthesizeAsync("A brand new line.", Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }
}
