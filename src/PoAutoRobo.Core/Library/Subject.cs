namespace PoAutoRobo.Core.Library;

/// <summary>What an episode is about. It decides how the script is written and where topics and facts come from.</summary>
public enum Subject
{
    /// <summary>The Unitree R1: scripts are grounded in the official repositories and held to the R1 accuracy rules.</summary>
    UnitreeR1,

    /// <summary>Anything else: the script is written from the topic text alone.</summary>
    General,
}

/// <summary>A passage from an official repository that a script was written from.</summary>
public sealed record GroundingSnippet(string Repo, string Path, string Url, string Text);

/// <summary>What to paste into the upload form beside the finished video.</summary>
public sealed record PublishNotes(IReadOnlyList<string> Titles, string Description, IReadOnlyList<string> Tags, IReadOnlyList<string> Hashtags);

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
