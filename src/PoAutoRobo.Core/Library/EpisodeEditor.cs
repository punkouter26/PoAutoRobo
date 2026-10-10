
namespace PoAutoRobo.Core.Library;

/// <summary>Every edit the studio makes to an episode. Each returns a new episode and touches only the clip it names.</summary>
public static class EpisodeEditor
{
    /// <summary>Share of words two versions of a line must have in common to count as the same subject without asking the model.</summary>
    public const double SameSubject = 0.85;

    /// <summary>A blank clip for the user to write, shown as a title card until it is given a picture.</summary>
    public static Clip NewClip() => new(
        Guid.NewGuid(), "New clip", Tier.B,
        new Dictionary<Tier, TierScript> { [Tier.B] = new("Write what the host says here.", "A comic panel that illustrates this clip.", "talking to the camera") },
        new VisualSpec(VisualKind.TitleCard, KindLocked: true), HostVisible: true);

    /// <summary>A second copy of a clip, with its pictures; its voice is recorded afresh.</summary>
    public static Clip Copy(Clip clip) => clip with { Id = Guid.NewGuid(), Title = clip.Title + " copy" };

    /// <summary>Puts <paramref name="clip"/> straight after the clip named.</summary>
    public static Episode AddClip(Episode episode, Guid afterId, Clip clip)
    {
        var at = episode.Clips.ToList().FindIndex(c => c.Id == afterId);
        if (at < 0)
            throw new ArgumentException($"No clip {afterId} in this episode.", nameof(afterId));
        return episode with { Clips = [.. episode.Clips.Take(at + 1), clip, .. episode.Clips.Skip(at + 1)] };
    }

    /// <exception cref="ArgumentException">It is the only clip: an episode always has at least one.</exception>
    public static Episode RemoveClip(Episode episode, Guid clipId) =>
        episode.Clips.Count == 1 && episode.Clips[0].Id == clipId
            ? throw new ArgumentException("An episode needs at least one clip.", nameof(clipId))
            : episode with { Clips = [.. episode.Clips.Where(c => c.Id != clipId)] };

    public static Episode Rename(Episode episode, string title) =>
        string.IsNullOrWhiteSpace(title) ? episode : episode with { Title = title.Trim() };

    public static Episode SetClipTitle(Episode episode, Guid clipId, string title) =>
        string.IsNullOrWhiteSpace(title) ? episode : Update(episode, clipId, clip => clip with { Title = title.Trim() });

    /// <summary>Replaces what the active depth's picture should show. A picture already drawn from the old words is out of date.</summary>
    public static Episode SetVisualPrompt(Episode episode, Guid clipId, string prompt) =>
        Update(episode, clipId, clip => string.IsNullOrWhiteSpace(prompt) || prompt.Trim() == clip.Active.VisualPrompt
            ? clip
            : clip with
            {
                Scripts = clip.Scripts.ToDictionary(s => s.Key, s => s.Key == clip.ActiveTier ? s.Value with { VisualPrompt = prompt.Trim() } : s.Value),
                Visual = clip.Visual with { Stale = clip.Visual.Stale || HasGeneratedMedia(clip) },
            });

    public static Episode Reorder(Episode episode, IReadOnlyList<Guid> order)
    {
        var byId = episode.Clips.ToDictionary(c => c.Id);
        if (order.Count != byId.Count || order.Distinct().Count() != order.Count || !order.All(byId.ContainsKey))
            throw new ArgumentException("The new order must list every clip exactly once.", nameof(order));
        return episode with { Clips = [.. order.Select(id => byId[id])] };
    }

    /// <exception cref="ArgumentException">The clip has not been written at that depth yet; add it with <see cref="AddTier"/> first.</exception>
    public static Episode SetTier(Episode episode, Guid clipId, Tier tier) =>
        Update(episode, clipId, clip => clip.ActiveTier == tier
            ? clip
            : !clip.Scripts.ContainsKey(tier)
            ? throw new ArgumentException($"This clip has not been written at depth {tier} yet.", nameof(tier))
            : clip with { ActiveTier = tier, Visual = clip.Visual with { Stale = clip.Visual.Stale || HasGeneratedMedia(clip) } });

    /// <summary>Gives a clip a depth it did not have. A depth already written is kept, so a slow reply cannot undo edits made to it.</summary>
    public static Episode AddTier(Episode episode, Guid clipId, Tier tier, TierScript script) =>
        Update(episode, clipId, clip => clip.Scripts.ContainsKey(tier)
            ? clip
            : clip with { Scripts = new Dictionary<Tier, TierScript>(clip.Scripts) { [tier] = script } });

    public static Episode SetHostVisible(Episode episode, Guid clipId, bool visible) =>
        Update(episode, clipId, clip => clip with { HostVisible = visible });

    /// <summary>Shows or hides the host in every clip at once.</summary>
    public static Episode SetHostEverywhere(Episode episode, bool visible) =>
        episode with { Clips = [.. episode.Clips.Select(c => c with { HostVisible = visible })] };

    /// <summary>Changes the art direction. Whatever was drawn in the old look is out of date.</summary>
    public static Episode SetLook(Episode episode, Look look) => episode.Look == look ? episode : episode with
    {
        Look = look,
        Clips = [.. episode.Clips.Select(c => HasGeneratedMedia(c) ? c with { Visual = c.Visual with { Stale = true } } : c)],
    };

    /// <summary>
    /// Replaces the active tier's dialogue. Narration follows the text on the next narrate pass; the picture is only
    /// marked stale when the model says the core action, tool or subject changed, and is never regenerated here.
    /// </summary>
    public static async Task<Episode> EditDialogueAsync(Episode episode, Guid clipId, string dialogue, IScriptWriter writer, CancellationToken ct)
    {
        dialogue = dialogue.Trim();
        if (dialogue.Length == 0)
            throw new ArgumentException("Dialogue cannot be empty.", nameof(dialogue));
        var clip = episode.Clips.FirstOrDefault(c => c.Id == clipId)
            ?? throw new ArgumentException($"No clip {clipId} in this episode.", nameof(clipId));
        var before = clip.Active.Dialogue;
        if (dialogue == before)
            return episode;

        // A clip with no picture yet has nothing to go stale: its picture request includes the dialogue as it is then.
        // A line that keeps nearly all its words (a typo fixed, a phrase tightened) is the same subject; nobody is asked.
        var stale = clip.Visual.Stale
            || (HasGeneratedMedia(clip) && Durations.SharedWords(before, dialogue) < SameSubject && await writer.CoreChangedAsync(before, dialogue, ct));
        return Update(episode, clipId, c => WithDialogue(c, dialogue) with { Visual = c.Visual with { Stale = stale } });
    }

    /// <summary>Swaps the clip's picture for the user's footage and takes the dialogue and pace that were fitted to it.</summary>
    public static Episode AttachVideo(Episode episode, Guid clipId, string videoPath, FitResult fit) =>
        Update(episode, clipId, c => WithDialogue(c, fit.Dialogue) with
        {
            NarrationRate = fit.Rate,
            Visual = new VisualSpec(VisualKind.UserVideo, KindLocked: true, UserVideoPath: videoPath),
        });

    public static Episode RemoveVideo(Episode episode, Guid clipId) =>
        Update(episode, clipId, c => c with { NarrationRate = 1.0, Visual = new VisualSpec(VisualKind.TitleCard) });

    // Attaches finished pictures to the clip they were drawn for, as that clip is now. Drawing takes a while, so the
    // clip may have moved on: footage or a different picture type wins and the pictures are dropped; changed words
    // keep the pictures but mark them out of date.
    public static Episode ApplyPicture(Episode episode, Clip requested, IReadOnlyList<string> paths)
    {
        var current = episode.Clips.FirstOrDefault(c => c.Id == requested.Id);
        if (current is null || current.Visual.Kind != requested.Visual.Kind || current.Visual.UserVideoPath is not null)
            return episode;
        var stillDescribesIt = current.Active == requested.Active && current.HostVisible == requested.HostVisible;
        // Another take: what the clip showed until now is kept beside it, so it can be gone back to.
        var earlier = requested.Visual.Take != current.Visual.Take && current.Visual.MediaPaths is { Count: > 0 } shown && !shown.SequenceEqual(paths)
            ? [.. current.Visual.EarlierTakes ?? [], shown]
            : current.Visual.EarlierTakes;
        return Update(episode, requested.Id, c => c with { Visual = c.Visual with { MediaPaths = paths, Stale = !stillDescribesIt, Take = requested.Visual.Take, EarlierTakes = earlier } });
    }

    /// <summary>The clip as it would be asked for once more: the same words, a fresh attempt at the picture.</summary>
    public static Clip NextTake(Clip clip) =>
        clip with { Visual = clip.Visual with { Take = Math.Max(clip.Visual.Take, clip.Visual.EarlierTakes?.Count ?? 0) + 1 } };

    /// <summary>Goes back to an earlier take: it and what the clip shows now change places. Nothing is made or paid for.</summary>
    public static Episode UseEarlierTake(Episode episode, Guid clipId, int index) =>
        Update(episode, clipId, c =>
        {
            if (c.Visual.EarlierTakes is not { } earlier || index < 0 || index >= earlier.Count)
                return c;
            var kept = earlier.ToList();
            var chosen = kept[index];
            if (c.Visual.MediaPaths is { Count: > 0 } shown) kept[index] = shown; else kept.RemoveAt(index);
            return c with { Visual = c.Visual with { MediaPaths = chosen, EarlierTakes = kept, Stale = false } };
        });

    /// <summary>Sets the music played under the narration; null takes it away.</summary>
    public static Episode SetMusic(Episode episode, string? path) => episode.MusicPath == path ? episode : episode with { MusicPath = path };

    // Swaps in one changed clip and leaves the rest of the episode as it is now, so a slow change to one clip
    // cannot undo edits made to the others while it was in progress.
    public static Episode ReplaceClip(Episode episode, Clip clip) =>
        episode.Clips.Any(c => c.Id == clip.Id) ? Update(episode, clip.Id, _ => clip) : episode;

    public static Episode SetMix(Episode episode, MixPercentages mix) => VisualMix.Assign(episode with { Mix = mix }, mix);

    /// <summary>Same proportions, dealt to different clips.</summary>
    public static Episode RerollMix(Episode episode, int newSeed) => VisualMix.Assign(episode with { MixSeed = newSeed }, episode.Mix);

    /// <summary>The user's own choice for one clip; mix changes and re-rolls leave it alone from then on.</summary>
    public static Episode SetKind(Episode episode, Guid clipId, VisualKind kind)
    {
        if (kind == VisualKind.UserVideo)
            throw new ArgumentException("Attach a video to make a clip use your own footage.", nameof(kind));
        return Update(episode, clipId, c => c with
        {
            Visual = c.Visual.Kind == kind ? c.Visual with { KindLocked = true } : new VisualSpec(kind, KindLocked: true),
        });
    }

    /// <summary>The clip with its active tier saying <paramref name="dialogue"/>; the other tiers are untouched.</summary>
    public static Clip WithDialogue(Clip clip, string dialogue) => clip with
    {
        Scripts = clip.Scripts.ToDictionary(s => s.Key, s => s.Key == clip.ActiveTier ? s.Value with { Dialogue = dialogue } : s.Value),
    };

    internal static Episode Update(Episode episode, Guid clipId, Func<Clip, Clip> change)
    {
        if (episode.Clips.All(c => c.Id != clipId))
            throw new ArgumentException($"No clip {clipId} in this episode.", nameof(clipId));
        return episode with { Clips = [.. episode.Clips.Select(c => c.Id == clipId ? change(c) : c)] };
    }

    private static bool HasGeneratedMedia(Clip clip) => clip.Visual.MediaPaths is { Count: > 0 };
}
