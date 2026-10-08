using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

/// <summary>Whole-episode conveniences the tests use; the app itself works one clip at a time.</summary>
internal static class TestExtensions
{
    /// <summary>Draws one clip's pictures and attaches them, the same two steps the app performs.</summary>
    public static async Task<Episode> GenerateAsync(this Visuals visuals, Episode episode, Guid clipId, CancellationToken ct)
    {
        var clip = episode.Clips.First(c => c.Id == clipId);
        return EpisodeEditor.ApplyPicture(episode, clip, await visuals.DrawAsync(clip, ct));
    }

    public static async Task<IReadOnlyList<Narration>> NarrateAsync(this EpisodeBuilder builder, Episode episode, string folder, CancellationToken ct)
    {
        var narrations = new List<Narration>(episode.Clips.Count);
        foreach (var clip in episode.Clips)
            narrations.Add(await builder.NarrateClipAsync(clip, folder, ct));
        return narrations;
    }
}
