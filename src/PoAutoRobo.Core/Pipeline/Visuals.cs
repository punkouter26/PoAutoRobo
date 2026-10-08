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

    public bool HasSheet => File.Exists(characterSheetPath);

    /// <summary>Makes several different takes on the host for the user to choose between. Nothing is locked yet.</summary>
    public async Task<IReadOnlyList<string>> CandidateSheetsAsync(int count, CancellationToken ct)
    {
        var candidates = new List<string>(count);
        for (var i = 1; i <= count; i++)
        {
            // The take number is part of the prompt, so each is a separate request and a separate cached file.
            var request = new ImageRequest(
                $"{Style} Character design sheet, take {i}: front view, side view and three-quarter view of one character on a plain white background. {HostInWords}", null);
            candidates.Add(await cache.GetOrCreateAsync([model, request.Size, request.Prompt, ""], ".png", scratch => images.GenerateAsync(request, scratch, ct)));
        }
        return candidates;
    }

    /// <summary>Makes this picture the host for every episode from now on.</summary>
    public void LockSheet(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(characterSheetPath))!);
        File.Copy(path, characterSheetPath, overwrite: true);
    }

    /// <summary>What to ask for. Host-visible clips carry the character sheet; off-screen clips ask for a diagram alone.</summary>
    public ImageRequest RequestFor(Clip clip)
    {
        var script = clip.Active;
        if (!clip.HostVisible)
            return new ImageRequest($"{Style} A full-frame technical diagram or simulation view with no characters or robots presenting. {script.VisualPrompt}", null);

        var host = HasSheet ? "The host is the robot in the reference image; keep its design, colours and proportions exactly." : HostInWords;
        return new ImageRequest($"{Style} {script.VisualPrompt} {host} The host is {script.Pose}. What the host is saying, for context only: {script.Dialogue}", HasSheet ? characterSheetPath : null);
    }

    /// <summary>Pictures in a panel sequence.</summary>
    public const int PanelCount = 3;

    private static readonly string[] Beats = ["the setup", "the key moment", "the result"];

    /// <summary>Generates the clip's picture or panel sequence, reusing cached files for identical requests.</summary>
    // ponytail: AI video clips get one still until the video service is wired in (T20).
    public async Task<Episode> GenerateAsync(Episode episode, Guid clipId, CancellationToken ct)
    {
        var clip = episode.Clips.FirstOrDefault(c => c.Id == clipId)
            ?? throw new ArgumentException($"No clip {clipId} in this episode.", nameof(clipId));
        var request = RequestFor(clip);
        var reference = request.ReferencePath is null ? "" : MediaCache.ContentHash(request.ReferencePath);

        var prompts = clip.Visual.Kind == VisualKind.MultiPanel
            ? Beats.Select((beat, i) => $"{request.Prompt} This is panel {i + 1} of {PanelCount} in a sequence and shows {beat}.")
            : [request.Prompt];
        var paths = new List<string>();
        foreach (var prompt in prompts)
        {
            var panel = request with { Prompt = prompt };
            paths.Add(await cache.GetOrCreateAsync([model, panel.Size, panel.Prompt, reference], ".png", scratch => images.GenerateAsync(panel, scratch, ct)));
        }
        return EpisodeEditor.SetVisual(episode, clipId, clip.Visual with { MediaPaths = paths, Stale = false });
    }
}