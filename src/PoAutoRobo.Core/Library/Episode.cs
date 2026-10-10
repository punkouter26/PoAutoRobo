namespace PoAutoRobo.Core.Library;

public enum Tier { A, B, C }

// New kinds go on the end: saved episodes name their kinds, and tests count on the order of the first five.
public enum VisualKind { Still, MultiPanel, AiVideo, TitleCard, UserVideo, Animation, Chart, KineticText, Stock, Parallax }

/// <summary>The art direction every generated picture in an episode shares.</summary>
public enum Look { Comic, Photoreal, FlatVector, Cinematic }

public sealed record TierScript(string Dialogue, string VisualPrompt, string Pose);

/// <param name="KindLocked">The user picked this kind by hand; mix re-rolls leave it alone.</param>
/// <param name="Stale">Dialogue changed the core action, so the generated media no longer matches.</param>
/// <param name="Take">Which attempt the media is: 0 the first, one more each time another is asked for.</param>
/// <param name="EarlierTakes">The media of the attempts not in use, oldest first, kept so any of them can be gone back to free.</param>
public sealed record VisualSpec(
    VisualKind Kind,
    bool KindLocked = false,
    bool Stale = false,
    string? UserVideoPath = null,
    IReadOnlyList<string>? MediaPaths = null,
    int Take = 0,
    IReadOnlyList<IReadOnlyList<string>>? EarlierTakes = null);

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
    /// A picture that stands for the clip: its first generated picture still on disk, or for footage (generated or
    /// the user's own) the frame taken from it. Null when there is neither.
    /// </summary>
    public string? Picture() => PictureOf((Visual.MediaPaths ?? []).Append(Visual.UserVideoPath).OfType<string>());

    /// <summary>The picture that stands for a set of media files: the first that is a picture, or a video's saved frame.</summary>
    public static string? PictureOf(IEnumerable<string> media) =>
        media.Select(path => IsPicture(path) ? path : PosterFor(path)).FirstOrDefault(File.Exists);

    /// <summary>Every media file the clip holds on to: what it shows now, its footage, and its earlier takes.</summary>
    public IEnumerable<string> AllMedia() =>
        (Visual.MediaPaths ?? []).Append(Visual.UserVideoPath).OfType<string>().Concat((Visual.EarlierTakes ?? []).SelectMany(take => take));

    private static bool IsPicture(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg";

    /// <summary>Where the frame taken from a video to stand for it is kept: beside the video.</summary>
    public static string PosterFor(string videoPath) => Path.ChangeExtension(videoPath, ".poster.png");

    /// <summary>Speaking pace multiplier; moves off 1.0 only to fit narration to the user's footage.</summary>
    public double NarrationRate { get; init; } = 1.0;
}

/// <summary>
/// Share of the plain clips (stills, panel sequences and title cards) given each of those three kinds; they must add
/// up to 100. The other kinds are never dealt: they are chosen clip by clip, by the script or by hand.
/// </summary>
public sealed record MixPercentages(int Still, int MultiPanel, int TitleCard)
{
    public static readonly MixPercentages Default = new(60, 25, 15);
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

    /// <summary>Episodes saved before there was a choice have no value here and are comic-book episodes.</summary>
    public Look Look { get; init; }

    /// <summary>A music track played quietly under the narration, inside the episode folder; null for none.</summary>
    public string? MusicPath { get; init; }

    /// <summary>The first picture any clip has on disk, used as the episode's thumbnail; null when there is none.</summary>
    public string? Cover() => Clips.Select(clip => clip.Picture()).FirstOrDefault(path => path is not null);
}
