namespace PoAutoRobo.Core.Models;

public enum Tier { A, B, C }

public enum VisualKind { Still, MultiPanel, AiVideo, TitleCard, UserVideo }

public sealed record TierScript(string Dialogue, string VisualPrompt, string Pose);

/// <param name="KindLocked">The user picked this kind by hand; mix re-rolls leave it alone.</param>
/// <param name="Stale">Dialogue changed the core action, so the generated media no longer matches.</param>
public sealed record VisualSpec(
    VisualKind Kind,
    bool KindLocked = false,
    bool Stale = false,
    string? UserVideoPath = null,
    IReadOnlyList<string>? MediaPaths = null);

public sealed record Clip(
    Guid Id,
    string Title,
    Tier ActiveTier,
    IReadOnlyDictionary<Tier, TierScript> Scripts,
    VisualSpec Visual,
    bool HostVisible)
{
    public TierScript Active => Scripts[ActiveTier];

    /// <summary>Speaking pace multiplier; moves off 1.0 only to fit narration to the user's footage.</summary>
    public double NarrationRate { get; init; } = 1.0;
}

/// <summary>Share of generated clips given each kind of picture; the four must add up to 100.</summary>
public sealed record MixPercentages(int Still, int MultiPanel, int AiVideo, int TitleCard)
{
    public static readonly MixPercentages Default = new(50, 20, 20, 10);
}

public sealed record Episode(
    string Title,
    string Topic,
    IReadOnlyList<Clip> Clips,
    int MixSeed)
{
    public CaptionStyle Captions { get; init; } = new();

    /// <summary>Episodes saved before any-topic episodes existed have no value here and are Unitree R1 episodes.</summary>
    public Subject Subject { get; init; }

    public MixPercentages Mix { get; init; } = MixPercentages.Default;
}
