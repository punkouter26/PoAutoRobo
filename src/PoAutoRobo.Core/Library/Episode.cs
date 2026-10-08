namespace PoAutoRobo.Core.Library;

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

    /// <summary>
    /// A picture that stands for the clip: its first generated picture still on disk, or for the user's own footage
    /// the frame taken from it. Null when there is neither.
    /// </summary>
    public string? Picture() =>
        Visual.MediaPaths?.FirstOrDefault(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        ?? (Visual.UserVideoPath is { } video && File.Exists(PosterFor(video)) ? PosterFor(video) : null);

    /// <summary>Where the frame taken from a video to stand for it is kept: beside the video.</summary>
    public static string PosterFor(string videoPath) => Path.ChangeExtension(videoPath, ".poster.png");

    /// <summary>Speaking pace multiplier; moves off 1.0 only to fit narration to the user's footage.</summary>
    public double NarrationRate { get; init; } = 1.0;
}

/// <summary>Share of generated clips given each kind of picture; the four must add up to 100.</summary>
public sealed record MixPercentages(int Still, int MultiPanel, int AiVideo, int TitleCard)
{
    public static readonly MixPercentages Default = new(60, 25, 0, 15); // no AI video until a video service is wired in
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

    /// <summary>The first picture any clip has on disk, used as the episode's thumbnail; null when there is none.</summary>
    public string? Cover() => Clips.Select(clip => clip.Picture()).FirstOrDefault(path => path is not null);
}
