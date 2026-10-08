using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Pipeline;

/// <summary>Turns a clip into a picture request and attaches the result to the clip.</summary>
/// <param name="characterSheetPath">The locked host design. Until it exists the host is described in words.</param>
/// <param name="model">Name of the picture model in use; part of the cache key so a model change regenerates.</param>
public sealed class Visuals(IImageGen images, MediaCache cache, string characterSheetPath, string model)
{
    public const string Style = "Bold, clean comic-book panel in 16:9 with flat colours and thick ink outlines. No speech bubbles, captions or lettering.";

    public const string HostInWords = "The host is a friendly, confident cartoon humanoid robot modelled on the Unitree R1: a slim white and grey body, black joints and a rounded dark visor for a face.";

    /// <summary>What to ask for. Host-visible clips carry the character sheet; off-screen clips ask for a diagram alone.</summary>
    public ImageRequest RequestFor(Clip clip)
    {
        var script = clip.Active;
        if (!clip.HostVisible)
            return new ImageRequest($"{Style} A full-frame technical diagram or simulation view with no characters or robots presenting. {script.VisualPrompt}", null);

        var hasSheet = File.Exists(characterSheetPath);
        var host = hasSheet ? "The host is the robot in the reference image; keep its design, colours and proportions exactly." : HostInWords;
        return new ImageRequest($"{Style} {script.VisualPrompt} {host} The host is {script.Pose}. What the host is saying, for context only: {script.Dialogue}", hasSheet ? characterSheetPath : null);
    }

    /// <summary>Generates the clip's picture, or reuses the cached one for an identical request.</summary>
    public async Task<Episode> GenerateAsync(Episode episode, Guid clipId, CancellationToken ct)
    {
        var clip = episode.Clips.FirstOrDefault(c => c.Id == clipId)
            ?? throw new ArgumentException($"No clip {clipId} in this episode.", nameof(clipId));
        var request = RequestFor(clip);
        var reference = request.ReferencePath is null ? "" : MediaCache.ContentHash(request.ReferencePath);

        var path = await cache.GetOrCreateAsync([model, request.Size, request.Prompt, reference], ".png", scratch => images.GenerateAsync(request, scratch, ct));
        return EpisodeEditor.Update(episode, clipId, c => c with { Visual = c.Visual with { MediaPaths = [path], Stale = false } });
    }
}
