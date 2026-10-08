using NSubstitute;

namespace PoAutoRobo.Core.Tests;

public sealed class ClipEditingTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly Episode _episode = ProjectStoreTests.NewEpisode(5);

    private Clip Target => _episode.Clips[1];

    private static FitResult Fit(string dialogue, double rate) => new(dialogue, rate, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(0.2), WithinTolerance: true);

    [Fact]
    public void Clips_can_be_added_copied_removed_and_renamed_and_a_new_picture_description_marks_an_old_picture_out_of_date()
    {
        var blank = EpisodeEditor.NewClip();
        var copy = EpisodeEditor.Copy(Target);

        var grown = EpisodeEditor.AddClip(EpisodeEditor.AddClip(_episode, Target.Id, blank), Target.Id, copy);

        Assert.Equal([_episode.Clips[0].Id, Target.Id, copy.Id, blank.Id, _episode.Clips[2].Id], grown.Clips.Take(5).Select(c => c.Id));
        Assert.Equal(Target.Title + " copy", copy.Title);
        Assert.Equal(Target.Scripts, copy.Scripts);
        Assert.Equal(_episode.Clips, EpisodeEditor.RemoveClip(EpisodeEditor.RemoveClip(grown, blank.Id), copy.Id).Clips);
        // An episode always has a clip to show.
        var one = ProjectStoreTests.NewEpisode(1);
        Assert.Throws<ArgumentException>(() => EpisodeEditor.RemoveClip(one, one.Clips[0].Id));

        var renamed = EpisodeEditor.SetClipTitle(EpisodeEditor.Rename(_episode, "  Standing up  "), Target.Id, " First steps ");
        Assert.Equal(("Standing up", "First steps"), (renamed.Title, renamed.Clips[1].Title));
        Assert.Same(_episode, EpisodeEditor.Rename(_episode, "   ")); // an empty name is not taken

        // A clip with no picture yet has nothing to go out of date; one with a picture has.
        var described = EpisodeEditor.SetVisualPrompt(_episode, Target.Id, "A robot on a tightrope.");
        Assert.Equal("A robot on a tightrope.", described.Clips[1].Active.VisualPrompt);
        Assert.False(described.Clips[1].Visual.Stale);
        var drawn = EpisodeEditor.Update(_episode, Target.Id, c => c with { Visual = c.Visual with { MediaPaths = ["panel.png"] } });
        Assert.True(EpisodeEditor.SetVisualPrompt(drawn, Target.Id, "A robot on a tightrope.").Clips[1].Visual.Stale);
    }

    [Fact]
    public void Undo_steps_back_one_edit_at_a_time_redo_returns_and_a_run_of_slider_ticks_is_undone_as_one()
    {
        var history = new EditHistory<string>(kept: 3);
        Assert.False(history.TryUndo("a", out _));

        history.Record("a");                 // a -> b
        history.Record("b", merges: true);   // b -> c, c -> d, d -> e: one drag of a slider
        history.Record("c", merges: true);
        history.Record("d", merges: true);

        Assert.True(history.TryUndo("e", out var beforeDrag));
        Assert.Equal("b", beforeDrag);
        Assert.True(history.TryUndo("b", out var first));
        Assert.Equal("a", first);
        Assert.False(history.CanUndo);
        Assert.True(history.TryRedo("a", out var again));
        Assert.Equal("b", again);

        // A new edit ends the chance to redo, and only the last few steps are kept.
        history.Record("b");
        Assert.False(history.CanRedo);
        foreach (var value in new[] { "c", "d", "e", "f" })
            history.Record(value);
        var steps = 0;
        for (var now = "g"; history.TryUndo(now, out now);) steps++;
        Assert.Equal(3, steps);
    }

    [Fact]
    public void Reorder_changes_the_running_order_and_nothing_else_and_rejects_a_list_that_drops_or_invents_clips()
    {
        var order = new[] { 3, 0, 4, 1, 2 }.Select(i => _episode.Clips[i].Id).ToList();

        var reordered = EpisodeEditor.Reorder(_episode, order);

        Assert.Equal(order, reordered.Clips.Select(c => c.Id));
        Assert.Equal(_episode.Clips.OrderBy(c => c.Id), reordered.Clips.OrderBy(c => c.Id));
        Assert.Equal(_episode with { Clips = reordered.Clips }, reordered);

        var missingOne = order.Skip(1).ToList();
        Assert.Throws<ArgumentException>(() => EpisodeEditor.Reorder(_episode, missingOne));
        Assert.Throws<ArgumentException>(() => EpisodeEditor.Reorder(_episode, [.. missingOne, Guid.NewGuid()]));
    }

    [Fact]
    public void Switching_tier_updates_dialogue_duration_and_visual_prompt_for_that_clip_only_and_marks_its_picture_stale()
    {
        var target = _episode.Clips[2];

        var edited = EpisodeEditor.SetTier(_episode, target.Id, Tier.C);

        var changed = edited.Clips[2];
        Assert.Equal(Tier.C, changed.ActiveTier);
        Assert.Equal(target.Scripts[Tier.C], changed.Active);
        Assert.Equal(Durations.Estimate(target.Scripts[Tier.C].Dialogue), Durations.Estimate(changed.Active.Dialogue));
        Assert.All(new[] { 0, 1, 3, 4 }, i => Assert.Same(_episode.Clips[i], edited.Clips[i]));
        Assert.False(changed.Visual.Stale); // no picture yet, so nothing to go out of date
        Assert.Throws<ArgumentException>(() => EpisodeEditor.SetTier(_episode, Guid.NewGuid(), Tier.A));

        // A picture already drawn was drawn from the old tier's prompt, so it is marked out of date.
        var withPicture = EpisodeEditor.Update(_episode, target.Id, c => c with { Visual = new VisualSpec(VisualKind.Still, MediaPaths: ["panel.png"]) });
        Assert.True(EpisodeEditor.SetTier(withPicture, target.Id, Tier.A).Clips[2].Visual.Stale);
        Assert.False(EpisodeEditor.SetTier(withPicture, target.Id, Tier.B).Clips[2].Visual.Stale); // already on B: nothing changed
    }

    [Fact]
    public void A_depth_not_written_yet_cannot_be_switched_to_until_it_is_added_and_adding_never_replaces_one_already_there()
    {
        // As a live script arrives: depth B only.
        var episode = EpisodeEditor.Update(_episode, Target.Id, c => c with { Scripts = new Dictionary<Tier, TierScript> { [Tier.B] = c.Scripts[Tier.B] } });
        var simple = new TierScript("Picture a bus.", "Analogy panel", "waving");

        Assert.Throws<ArgumentException>(() => EpisodeEditor.SetTier(episode, Target.Id, Tier.A));

        var added = EpisodeEditor.AddTier(episode, Target.Id, Tier.A, simple);
        Assert.Equal(simple, EpisodeEditor.SetTier(added, Target.Id, Tier.A).Clips[1].Active);
        Assert.Equal(Tier.B, added.Clips[1].ActiveTier); // adding a depth does not switch to it

        // A slow reply must not undo what the user has typed into that depth since.
        var late = EpisodeEditor.AddTier(added, Target.Id, Tier.B, new TierScript("Late reply.", "p", "p"));
        Assert.Equal(Target.Scripts[Tier.B], late.Clips[1].Scripts[Tier.B]);
    }

    // ---- A picture that arrives after the clip has moved on ----

    [Fact]
    public async Task A_finished_picture_clears_stale_on_an_unchanged_clip_but_is_marked_out_of_date_if_the_words_changed_meanwhile()
    {
        var stale = EpisodeEditor.Update(_episode, Target.Id, c => c with { Visual = new VisualSpec(VisualKind.Still, Stale: true) });

        var unchanged = EpisodeEditor.ApplyPicture(stale, stale.Clips[1], ["new.png"]).Clips[1];

        Assert.Equal(["new.png"], unchanged.Visual.MediaPaths);
        Assert.False(unchanged.Visual.Stale);

        var edited = await EpisodeEditor.EditDialogueAsync(_episode, Target.Id, "Now something else entirely.", Substitute.For<IScriptWriter>(), Ct);

        var moved = EpisodeEditor.ApplyPicture(edited, Target, ["old-words.png"]).Clips[1];

        Assert.Equal(["old-words.png"], moved.Visual.MediaPaths);
        Assert.True(moved.Visual.Stale);
        Assert.Equal("Now something else entirely.", moved.Active.Dialogue);
    }

    [Fact]
    public void A_picture_never_overrides_footage_a_picture_type_chosen_while_it_was_being_drawn_or_a_clip_that_is_gone()
    {
        var withVideo = EpisodeEditor.AttachVideo(_episode, Target.Id, "lab.mp4", Fit("Fitted.", 1.05));
        var retyped = EpisodeEditor.SetKind(_episode, Target.Id, VisualKind.TitleCard);
        var other = ProjectStoreTests.NewEpisode(2);

        Assert.Equal(withVideo, EpisodeEditor.ApplyPicture(withVideo, Target, ["late.png"]));
        Assert.Equal(retyped, EpisodeEditor.ApplyPicture(retyped, Target, ["late.png"]));
        Assert.Same(other, EpisodeEditor.ApplyPicture(other, Target, ["late.png"]));
    }

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

    // ---- The user's own footage ----

    [Fact]
    public void Attaching_footage_sets_the_clip_to_my_video_with_the_fitted_words_and_rate_and_removing_it_resets_picture_and_pace()
    {
        var attached = EpisodeEditor.AttachVideo(_episode, Target.Id, @"imports\lab.mp4", Fit("Fitted words.", 1.05));

        var clip = attached.Clips[1];
        Assert.Equal(new VisualSpec(VisualKind.UserVideo, KindLocked: true, UserVideoPath: @"imports\lab.mp4"), clip.Visual);
        Assert.Equal("Fitted words.", clip.Active.Dialogue);
        Assert.Equal(1.05, clip.NarrationRate);
        Assert.Same(_episode.Clips[0], attached.Clips[0]);

        var removed = EpisodeEditor.RemoveVideo(attached, Target.Id).Clips[1];

        Assert.Equal(new VisualSpec(VisualKind.TitleCard), removed.Visual);
        Assert.Equal(1.0, removed.NarrationRate);
        Assert.Equal("Fitted words.", removed.Active.Dialogue); // the words stay; only the picture and pace reset
    }
}
