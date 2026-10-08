namespace PoAutoRobo.Core.Models;

/// <summary>A passage from an official repository that a script was written from.</summary>
public sealed record GroundingSnippet(string Repo, string Path, string Url, string Text);

/// <summary>How many clips an episode should have. One clip makes a half-minute video for trying the whole workflow quickly.</summary>
public sealed record EpisodeLength
{
    public static readonly EpisodeLength Full = new(15, 20);
    public static readonly EpisodeLength Short = new(5, 5);
    public static readonly EpisodeLength TwoClips = new(2, 2);
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
