using NSubstitute;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

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

    [Fact]
    public async Task Edit_replaces_the_active_tier_dialogue_for_that_clip_only()
    {
        var edited = await EpisodeEditor.EditDialogueAsync(_episode, Target.Id, "New words entirely.", _writer, Ct);

        Assert.Equal("New words entirely.", edited.Clips[1].Active.Dialogue);
        Assert.Equal(Target.Scripts[Tier.A], edited.Clips[1].Scripts[Tier.A]);
        Assert.Equal(Target.Scripts[Tier.C], edited.Clips[1].Scripts[Tier.C]);
        Assert.Same(_episode.Clips[0], edited.Clips[0]);
        Assert.Same(_episode.Clips[2], edited.Clips[2]);
    }

    [Fact]
    public async Task Phrasing_change_keeps_the_visual()
    {
        _writer.CoreChangedAsync(Target.Active.Dialogue, "Reworded line.", Ct).Returns(false);

        var edited = await EpisodeEditor.EditDialogueAsync(_episode, Target.Id, "Reworded line.", _writer, Ct);

        Assert.False(edited.Clips[1].Visual.Stale);
        Assert.Equal(Target.Visual, edited.Clips[1].Visual);
    }

    [Fact]
    public async Task Core_action_change_marks_the_visual_stale_without_regenerating_it()
    {
        _writer.CoreChangedAsync(Target.Active.Dialogue, "Now inspect the waist actuator.", Ct).Returns(true);

        var edited = await EpisodeEditor.EditDialogueAsync(_episode, Target.Id, "Now inspect the waist actuator.", _writer, Ct);

        Assert.True(edited.Clips[1].Visual.Stale);
        Assert.Equal(Target.Visual.MediaPaths, edited.Clips[1].Visual.MediaPaths);
    }

    [Fact]
    public async Task Unchanged_or_whitespace_only_edit_does_nothing_and_asks_nobody()
    {
        var same = await EpisodeEditor.EditDialogueAsync(_episode, Target.Id, "  " + Target.Active.Dialogue + "\n", _writer, Ct);

        Assert.Same(_episode, same);
        await _writer.DidNotReceiveWithAnyArgs().CoreChangedAsync(default!, default!, default);
    }

    [Fact]
    public async Task Empty_dialogue_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => EpisodeEditor.EditDialogueAsync(_episode, Target.Id, "   ", _writer, Ct));
    }

    [Fact]
    public async Task Editing_one_clip_re_synthesises_only_that_clip()
    {
        var narrator = Substitute.For<INarrator>();
        narrator.SynthesizeAsync(default!, default!, default, default).ReturnsForAnyArgs(call =>
        {
            var path = call.ArgAt<string>(1);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            NarratorTests.WriteSilentWav(path, 1.0);
            return new Narration(path, TimeSpan.FromSeconds(1), [new WordTiming("x", TimeSpan.Zero, TimeSpan.FromSeconds(1))]);
        });
        var builder = new EpisodeBuilder(narrator, new FfmpegRunner("ffmpeg.exe"));
        await builder.NarrateAsync(_episode, _folder, Ct);
        narrator.ClearReceivedCalls();

        var edited = await EpisodeEditor.EditDialogueAsync(_episode, Target.Id, "A brand new line.", _writer, Ct);
        await builder.NarrateAsync(edited, _folder, Ct);

        await narrator.Received(1).SynthesizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await narrator.Received(1).SynthesizeAsync("A brand new line.", Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }
}
