using PoAutoRobo.Core.Models;

namespace PoAutoRobo.Core.Services;

public interface IScriptWriter
{
    /// <summary>The longest topic sent to the model. A larger paste is cut here so it cannot run up a large bill.</summary>
    const int MaxTopicLength = 8000;

    /// <summary>Writes every clip at depth B only; the other depths are written when first asked for.</summary>
    /// <param name="clipsWritten">Told how many clips have been written so far, as the script arrives.</param>
    Task<Episode> WriteEpisodeAsync(string topic, IReadOnlyList<GroundingSnippet> grounding, EpisodeLength length, CancellationToken ct, IProgress<int>? clipsWritten = null);

    /// <summary>Writes one clip at a depth it does not have yet, from the depth it is on now.</summary>
    Task<TierScript> WriteTierAsync(string topic, Clip clip, Tier tier, CancellationToken ct);

    /// <summary>True when the edit changes the core action, tool or physical subject, so the visual no longer fits.</summary>
    Task<bool> CoreChangedAsync(string oldDialogue, string newDialogue, CancellationToken ct);

    Task<string> RewriteToLengthAsync(string dialogue, int targetWords, CancellationToken ct);
}
