using PoAutoRobo.Core.Models;

namespace PoAutoRobo.Core.Services;

public sealed record GroundingSnippet(string Repo, string Path, string Url, string Text);

public interface IScriptWriter
{
    Task<Episode> WriteEpisodeAsync(string topic, IReadOnlyList<GroundingSnippet> grounding, CancellationToken ct);

    /// <summary>True when the edit changes the core action, tool or physical subject, so the visual no longer fits.</summary>
    Task<bool> CoreChangedAsync(string oldDialogue, string newDialogue, CancellationToken ct);

    Task<string> RewriteToLengthAsync(string dialogue, int targetWords, CancellationToken ct);
}
