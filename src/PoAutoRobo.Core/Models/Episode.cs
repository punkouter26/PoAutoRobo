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
}

public sealed record Episode(
    string Title,
    string Topic,
    IReadOnlyList<Clip> Clips,
    int MixSeed)
{
    public CaptionStyle Captions { get; init; } = new();
}
