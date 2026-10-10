
namespace PoAutoRobo.Core.Script;

/// <summary>Something an editor would change about one clip's words, found by reading the whole script.</summary>
/// <param name="Clip">The clip's place in the running order, counting from 1.</param>
public sealed record ClipNote(int Clip, string Note);

public interface IScriptWriter
{
    /// <summary>The longest topic sent to the model. A larger paste is cut here so it cannot run up a large bill.</summary>
    const int MaxTopicLength = 8000;

    /// <summary>Writes every clip at depth B only; the other depths are written when first asked for.</summary>
    /// <param name="clipsWritten">Given the titles of the clips finished so far, as the script arrives.</param>
    Task<Episode> WriteEpisodeAsync(string topic, Subject subject, IReadOnlyList<GroundingSnippet> grounding, EpisodeLength length, CancellationToken ct, IProgress<IReadOnlyList<string>>? clipsWritten = null);

    /// <summary>Writes one clip at a depth it does not have yet, from the depth it is on now.</summary>
    Task<TierScript> WriteTierAsync(string topic, Subject subject, Clip clip, Tier tier, CancellationToken ct);

    /// <summary>True when the edit changes the core action, tool or physical subject, so the visual no longer fits.</summary>
    Task<bool> CoreChangedAsync(string oldDialogue, string newDialogue, CancellationToken ct);

    Task<string> RewriteToLengthAsync(string dialogue, int targetWords, CancellationToken ct);

    /// <summary>Writes an animated scene in code: a diagram, a chart or moving text, to run for the length asked.</summary>
    /// <param name="failed">The scene written last time, when its code did not run; it is then written again with <paramref name="problem"/> in hand.</param>
    Task<Scene> WriteSceneAsync(SceneRequest request, CancellationToken ct, Scene? failed = null, string? problem = null);

    /// <summary>Reads the whole script as an editor would and notes the clips worth changing; none when it reads well.</summary>
    Task<IReadOnlyList<ClipNote>> ReviewAsync(Episode episode, CancellationToken ct);

    /// <summary>Titles, a description and tags to upload with the finished video, written from its script.</summary>
    Task<PublishNotes> WritePublishNotesAsync(Episode episode, CancellationToken ct);
}
