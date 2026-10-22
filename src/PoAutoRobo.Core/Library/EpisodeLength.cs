namespace PoAutoRobo.Core.Library;

/// <summary>
/// How many clips an episode should have, besides any the owner's footage needs. Standard aims at the series' three
/// to five minutes; one clip makes a half-minute video for trying the whole workflow quickly.
/// </summary>
public sealed record EpisodeLength
{
    public static readonly EpisodeLength Standard = new(6, 10);
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
