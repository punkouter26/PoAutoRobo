using PoAutoRobo.Core.Models;

namespace PoAutoRobo.Core.Services;

public sealed record GroundingSnippet(string Repo, string Path, string Url, string Text);

/// <summary>How many clips an episode should have. One clip makes a half-minute video for trying the whole workflow quickly.</summary>
public sealed record EpisodeLength
{
    public static readonly EpisodeLength Full = new(15, 20);
    public static readonly EpisodeLength Short = new(5, 5);
    public static readonly EpisodeLength QuickTest = new(1, 1);

    public EpisodeLength(int minClips, int maxClips)
    {
        if (minClips < 1 || maxClips > 20 || minClips > maxClips)
            throw new ArgumentOutOfRangeException(nameof(minClips), "An episode has between 1 and 20 clips.");
        (MinClips, MaxClips) = (minClips, maxClips);
    }

    public int MinClips { get; }

    public int MaxClips { get; }

    /// <summary>The count in words, for prompts: "exactly 1 clip", "between 15 and 20 clips".</summary>
    public string InWords => MinClips == MaxClips ? $"exactly {MinClips} {(MinClips == 1 ? "clip" : "clips")}" : $"between {MinClips} and {MaxClips} clips";
}

public interface IScriptWriter
{
    Task<Episode> WriteEpisodeAsync(string topic, IReadOnlyList<GroundingSnippet> grounding, EpisodeLength length, CancellationToken ct);

    /// <summary>True when the edit changes the core action, tool or physical subject, so the visual no longer fits.</summary>
    Task<bool> CoreChangedAsync(string oldDialogue, string newDialogue, CancellationToken ct);

    Task<string> RewriteToLengthAsync(string dialogue, int targetWords, CancellationToken ct);
}
