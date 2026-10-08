using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Pipeline;

/// <summary>Every edit the studio makes to an episode. Each returns a new episode and touches only the clip it names.</summary>
public static class EpisodeEditor
{
    public static Episode Reorder(Episode episode, IReadOnlyList<Guid> order)
    {
        var byId = episode.Clips.ToDictionary(c => c.Id);
        if (order.Count != byId.Count || order.Distinct().Count() != order.Count || !order.All(byId.ContainsKey))
            throw new ArgumentException("The new order must list every clip exactly once.", nameof(order));
        return episode with { Clips = [.. order.Select(id => byId[id])] };
    }

    public static Episode SetTier(Episode episode, Guid clipId, Tier tier) =>
        Update(episode, clipId, clip => clip.ActiveTier == tier
            ? clip
            : clip with { ActiveTier = tier, Visual = clip.Visual with { Stale = clip.Visual.Stale || HasGeneratedMedia(clip) } });

    public static Episode SetHostVisible(Episode episode, Guid clipId, bool visible) =>
        Update(episode, clipId, clip => clip with { HostVisible = visible });

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

        // ponytail: a clip with no picture yet keeps its old visual prompt after a core change; picture requests add the dialogue (T18).
        var stale = clip.Visual.Stale || (HasGeneratedMedia(clip) && await writer.CoreChangedAsync(before, dialogue, ct));
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
        return Update(episode, requested.Id, c => c with { Visual = c.Visual with { MediaPaths = paths, Stale = !stillDescribesIt } });
    }

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

    /// <summary>Replaces one clip's picture settings, leaving any edits made to the rest of the episode meanwhile.</summary>
    public static Episode SetVisual(Episode episode, Guid clipId, VisualSpec visual) => Update(episode, clipId, c => c with { Visual = visual });

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
