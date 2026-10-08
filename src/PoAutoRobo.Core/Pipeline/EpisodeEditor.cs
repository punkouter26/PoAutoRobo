using PoAutoRobo.Core.Models;

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

    internal static Episode Update(Episode episode, Guid clipId, Func<Clip, Clip> change)
    {
        if (episode.Clips.All(c => c.Id != clipId))
            throw new ArgumentException($"No clip {clipId} in this episode.", nameof(clipId));
        return episode with { Clips = [.. episode.Clips.Select(c => c.Id == clipId ? change(c) : c)] };
    }

    private static bool HasGeneratedMedia(Clip clip) => clip.Visual.MediaPaths is { Count: > 0 };
}
