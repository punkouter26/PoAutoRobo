using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

public sealed class ClipEditingTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;
    private readonly Episode _episode = ProjectStoreTests.NewEpisode(5);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Reorder_changes_the_running_order_and_nothing_else()
    {
        var order = new[] { 3, 0, 4, 1, 2 }.Select(i => _episode.Clips[i].Id).ToList();

        var reordered = EpisodeEditor.Reorder(_episode, order);

        Assert.Equal(order, reordered.Clips.Select(c => c.Id));
        Assert.Equal(_episode.Clips.OrderBy(c => c.Id), reordered.Clips.OrderBy(c => c.Id));
        Assert.Equal(_episode with { Clips = reordered.Clips }, reordered);
    }

    [Fact]
    public void Reorder_survives_save_and_reload()
    {
        var order = _episode.Clips.Reverse().Select(c => c.Id).ToList();

        ProjectStore.Save(EpisodeEditor.Reorder(_episode, order), _folder);

        Assert.Equal(order, ProjectStore.Load(_folder).Clips.Select(c => c.Id));
    }

    [Fact]
    public void Reorder_rejects_a_list_that_drops_or_invents_clips()
    {
        var missingOne = _episode.Clips.Skip(1).Select(c => c.Id).ToList();
        var withStranger = _episode.Clips.Skip(1).Select(c => c.Id).Append(Guid.NewGuid()).ToList();

        Assert.Throws<ArgumentException>(() => EpisodeEditor.Reorder(_episode, missingOne));
        Assert.Throws<ArgumentException>(() => EpisodeEditor.Reorder(_episode, withStranger));
    }

    [Fact]
    public void Switching_tier_updates_dialogue_duration_and_visual_prompt_for_that_clip_only()
    {
        var target = _episode.Clips[2];

        var edited = EpisodeEditor.SetTier(_episode, target.Id, Tier.C);

        var changed = edited.Clips[2];
        Assert.Equal(Tier.C, changed.ActiveTier);
        Assert.Equal(target.Scripts[Tier.C].Dialogue, changed.Active.Dialogue);
        Assert.Equal(target.Scripts[Tier.C].VisualPrompt, changed.Active.VisualPrompt);
        Assert.Equal(Durations.Estimate(target.Scripts[Tier.C].Dialogue), Durations.Estimate(changed.Active.Dialogue));
        Assert.All(new[] { 0, 1, 3, 4 }, i => Assert.Same(_episode.Clips[i], edited.Clips[i]));
    }

    [Fact]
    public void Switching_tier_marks_a_generated_visual_stale_because_its_prompt_changed()
    {
        var withMedia = _episode.Clips[0] with { Visual = new VisualSpec(VisualKind.Still, MediaPaths: ["panel.png"]) };
        var episode = _episode with { Clips = [withMedia, .. _episode.Clips.Skip(1)] };

        Assert.True(EpisodeEditor.SetTier(episode, withMedia.Id, Tier.A).Clips[0].Visual.Stale);
        Assert.False(EpisodeEditor.SetTier(episode, withMedia.Id, Tier.B).Clips[0].Visual.Stale); // already on B: nothing changed
    }

    [Fact]
    public void Host_toggle_changes_only_that_clip_and_persists()
    {
        var target = _episode.Clips[1];

        var edited = EpisodeEditor.SetHostVisible(_episode, target.Id, false);
        ProjectStore.Save(edited, _folder);

        Assert.False(ProjectStore.Load(_folder).Clips[1].HostVisible);
        Assert.All(new[] { 0, 2, 3, 4 }, i => Assert.Same(_episode.Clips[i], edited.Clips[i]));
    }

    [Fact]
    public void Editing_an_unknown_clip_is_an_error()
    {
        Assert.Throws<ArgumentException>(() => EpisodeEditor.SetTier(_episode, Guid.NewGuid(), Tier.A));
    }
}
