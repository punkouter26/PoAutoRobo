
namespace PoAutoRobo.Core.Script;

/// <summary>Something an editor would change about one clip's words, found by reading the whole script.</summary>
/// <param name="Clip">The clip's place in the running order, counting from 1.</param>
public sealed record ClipNote(int Clip, string Note);

/// <summary>Another picture for a clip whose own picture a picture model would not draw.</summary>
/// <param name="Diagram">A picture of the same idea that it will draw: a diagram, the tools, the place.</param>
/// <param name="SearchWords">A few plain words that would find a photograph of the place or the object.</param>
public sealed record SaferPicture(string Diagram, string SearchWords);

/// <summary>What a model saw happen in one of the owner's films, from a few of its frames.</summary>
/// <param name="Timeline">What happens, a line to each stretch, each line starting with its seconds.</param>
/// <param name="Note">One line on what the film shows, as the owner might label it.</param>
/// <param name="Highlight">The second of the most telling moment: a fall, a catch, a success.</param>
public sealed record FootageSeen(string Timeline, string Note, double Highlight);

/// <summary>One frame taken from a film or a scene, and when in it.</summary>
public sealed record Frame(TimeSpan At, string Path);

/// <summary>What a look at a drawn scene found.</summary>
/// <param name="Problem">What to move or change, said as a designer would; empty when it is fine.</param>
public sealed record SceneVerdict(bool Fine, string Problem);

public interface IScriptWriter
{
    /// <summary>The longest topic sent to the model. A larger paste is cut here so it cannot run up a large bill.</summary>
    const int MaxTopicLength = 8000;

    /// <summary>
    /// Writes the lesson as an episode told in its beats, with each of the owner's footage files in one clip (the
    /// files are not copied here). Every clip is at depth B only; the other depths are written when first asked for.
    /// </summary>
    /// <param name="clipsWritten">Given the titles of the clips finished so far, as the script arrives.</param>
    Task<Episode> WriteEpisodeAsync(Lesson lesson, IReadOnlyList<GroundingSnippet> grounding, EpisodeLength length, CancellationToken ct, IProgress<IReadOnlyList<string>>? clipsWritten = null);

    /// <summary>Writes one clip at a depth it does not have yet, from the depth it is on now.</summary>
    Task<TierScript> WriteTierAsync(string topic, Clip clip, Tier tier, CancellationToken ct);

    /// <summary>True when the edit changes the core action, tool or physical subject, so the visual no longer fits.</summary>
    Task<bool> CoreChangedAsync(string oldDialogue, string newDialogue, CancellationToken ct);

    Task<string> RewriteToLengthAsync(string dialogue, int targetWords, CancellationToken ct);

    /// <summary>Writes an animated scene in code: a diagram, a chart or moving text, to run for the length asked.</summary>
    /// <param name="failed">The scene written last time, when its code did not run; it is then written again with <paramref name="problem"/> in hand.</param>
    Task<Scene> WriteSceneAsync(SceneRequest request, CancellationToken ct, Scene? failed = null, string? problem = null);

    /// <summary>Thinks of another picture for a clip whose picture the picture model declined to draw.</summary>
    Task<SaferPicture> RethinkPictureAsync(Clip clip, CancellationToken ct);

    /// <summary>Reads the whole script as an editor would and notes the clips worth changing; none when it reads well.</summary>
    Task<IReadOnlyList<ClipNote>> ReviewAsync(Episode episode, CancellationToken ct);

    /// <summary>Rewrites one clip's words to answer the editor's note on them, keeping what the note does not mention.</summary>
    Task<string> ReviseAsync(Clip clip, string note, CancellationToken ct);

    /// <summary>What happens in one of the owner's films, seen from a few of its frames; null when nothing here can see pictures.</summary>
    /// <param name="note">The owner's own line on it, when they wrote one.</param>
    Task<FootageSeen?> WatchAsync(IReadOnlyList<Frame> frames, string note, CancellationToken ct);

    /// <summary>Looks at a scene's frame once it has built, for labels cut off, overlapping or under the captions.</summary>
    Task<SceneVerdict> CheckSceneAsync(string framePath, CancellationToken ct);

    /// <summary>How a picture of the host differs from its locked design; null when it keeps it, or nothing can tell.</summary>
    Task<string?> CheckHostAsync(string picturePath, string sheetPath, CancellationToken ct);

    /// <summary>The words a lesson's passages would be found by in the official repositories, besides its own.</summary>
    Task<IReadOnlyList<string>> SearchTermsAsync(string topic, CancellationToken ct);

    /// <summary>Titles, a description and tags to upload with the finished video, written from its script.</summary>
    Task<PublishNotes> WritePublishNotesAsync(Episode episode, CancellationToken ct);

    /// <summary>
    /// Three ten-second teasers for other video sites, cut from the finished episode, held to <see cref="Teasers"/>'
    /// rules. Where too few suggestions keep to them, teasers made by the rules alone fill in.
    /// </summary>
    /// <param name="lengths">How long each clip runs in the finished episode.</param>
    Task<IReadOnlyList<Teaser>> SuggestTeasersAsync(Episode episode, IReadOnlyDictionary<Guid, TimeSpan> lengths, CancellationToken ct);
}
