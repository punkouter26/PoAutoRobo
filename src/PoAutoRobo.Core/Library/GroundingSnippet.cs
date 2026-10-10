namespace PoAutoRobo.Core.Library;

/// <summary>A passage from an official repository that a script was written from.</summary>
public sealed record GroundingSnippet(string Repo, string Path, string Url, string Text);
