using NSubstitute;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

/// <summary>Regression tests for problems found in code review: lost edits and collisions when things overlap.</summary>
public sealed class ReviewFixTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;
    private readonly Episode _episode;

    public ReviewFixTests()
    {
        var episode = ProjectStoreTests.NewEpisode(3);
        _episode = episode with { Clips = [.. episode.Clips.Select(c => c with { Visual = new VisualSpec(VisualKind.Still) })] };
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private Clip Target => _episode.Clips[1];

    // ---- A picture that arrives after the clip has moved on ----

    [Fact]
    public void A_finished_picture_is_attached_to_an_unchanged_clip_and_clears_stale()
    {
        var stale = EpisodeEditor.SetVisual(_episode, Target.Id, new VisualSpec(VisualKind.Still, Stale: true));

        var clip = EpisodeEditor.ApplyPicture(stale, stale.Clips[1], ["new.png"]).Clips[1];

        Assert.Equal(["new.png"], clip.Visual.MediaPaths);
        Assert.False(clip.Visual.Stale);
    }

    [Fact]
    public void A_picture_never_overrides_footage_or_a_picture_type_chosen_while_it_was_being_drawn()
    {
        var withVideo = EpisodeEditor.AttachVideo(_episode, Target.Id, "lab.mp4", new FitResult("Fitted.", 1.05, TimeSpan.FromSeconds(9), TimeSpan.Zero, true));
        var retyped = EpisodeEditor.SetKind(_episode, Target.Id, VisualKind.TitleCard);

        Assert.Equal(withVideo, EpisodeEditor.ApplyPicture(withVideo, Target, ["late.png"]));
        Assert.Equal(retyped, EpisodeEditor.ApplyPicture(retyped, Target, ["late.png"]));
    }

    [Fact]
    public async Task A_picture_drawn_for_dialogue_that_has_since_changed_is_kept_but_marked_out_of_date()
    {
        var writer = Substitute.For<IScriptWriter>();
        var edited = await EpisodeEditor.EditDialogueAsync(_episode, Target.Id, "Now something else entirely.", writer, Ct);

        var clip = EpisodeEditor.ApplyPicture(edited, Target, ["old-words.png"]).Clips[1];

        Assert.Equal(["old-words.png"], clip.Visual.MediaPaths);
        Assert.True(clip.Visual.Stale);
        Assert.Equal("Now something else entirely.", clip.Active.Dialogue);
    }

    [Fact]
    public void A_picture_for_a_clip_that_no_longer_exists_changes_nothing()
    {
        var other = ProjectStoreTests.NewEpisode(2);

        Assert.Same(other, EpisodeEditor.ApplyPicture(other, Target, ["late.png"]));
    }

    // ---- One clip's change must not undo other edits made meanwhile ----

    [Fact]
    public void Replacing_one_clip_keeps_every_other_edit_made_in_the_meantime()
    {
        var changedClip = EpisodeEditor.WithDialogue(Target, "Edited words.");
        var meanwhile = EpisodeEditor.Reorder(EpisodeEditor.SetTier(_episode, _episode.Clips[0].Id, Tier.C), [.. _episode.Clips.Reverse().Select(c => c.Id)]);

        var merged = EpisodeEditor.ReplaceClip(meanwhile, changedClip);

        Assert.Equal(meanwhile.Clips.Select(c => c.Id), merged.Clips.Select(c => c.Id));        // the reorder survived
        Assert.Equal(Tier.C, merged.Clips.First(c => c.Id == _episode.Clips[0].Id).ActiveTier); // so did the tier change
        Assert.Equal("Edited words.", merged.Clips.First(c => c.Id == Target.Id).Active.Dialogue);
    }

    // ---- Narration asked for twice at once ----

    [Fact]
    public async Task Two_requests_for_the_same_narration_at_once_speak_it_only_once()
    {
        var narrator = Substitute.For<INarrator>();
        narrator.SynthesizeAsync(default!, default!, default, default).ReturnsForAnyArgs(async call =>
        {
            var path = call.ArgAt<string>(1);
            await Task.Delay(150); // long enough for the second request to arrive while the first is still speaking
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            NarratorTests.WriteSilentWav(path, 1.0);
            return new Narration(path, TimeSpan.FromSeconds(1), [new WordTiming("x", TimeSpan.Zero, TimeSpan.FromSeconds(1))]);
        });
        var builder = new EpisodeBuilder(narrator, new FfmpegRunner("ffmpeg.exe"));

        var both = await Task.WhenAll(builder.NarrateClipAsync(Target, _folder, Ct), builder.NarrateClipAsync(Target, _folder, Ct));

        await narrator.ReceivedWithAnyArgs(1).SynthesizeAsync(default!, default!, default, default);
        Assert.Equal(both[0].AudioPath, both[1].AudioPath);
    }

    // ---- Episode folders ----

    [Fact]
    public void A_second_episode_with_the_same_title_gets_its_own_folder()
    {
        var first = ProjectStore.NewFolder(_folder, "Balancing the R1");
        ProjectStore.Save(_episode, first);

        var second = ProjectStore.NewFolder(_folder, "Balancing the R1");
        ProjectStore.Save(_episode, second);

        Assert.Equal(Path.Combine(_folder, "balancing-the-r1"), first);
        Assert.Equal(Path.Combine(_folder, "balancing-the-r1-2"), second);
        Assert.Equal(Path.Combine(_folder, "balancing-the-r1-3"), ProjectStore.NewFolder(_folder, "Balancing the R1"));
    }

    [Fact]
    public void A_very_long_title_still_gives_a_folder_name_the_file_system_accepts()
    {
        var folder = ProjectStore.NewFolder(_folder, new string('a', 400) + " with a long summary pasted in");

        Assert.InRange(Path.GetFileName(folder).Length, 1, 60);
        Directory.CreateDirectory(folder); // must not throw
    }

    [Theory]
    [InlineData("")]
    [InlineData("???")]
    public void A_title_with_nothing_usable_falls_back_to_a_plain_name(string title)
    {
        Assert.Equal(Path.Combine(_folder, "episode"), ProjectStore.NewFolder(_folder, title));
    }
}
