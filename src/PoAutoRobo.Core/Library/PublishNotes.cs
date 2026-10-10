namespace PoAutoRobo.Core.Library;

/// <summary>What to paste into the upload form beside the finished video.</summary>
public sealed record PublishNotes(IReadOnlyList<string> Titles, string Description, IReadOnlyList<string> Tags, IReadOnlyList<string> Hashtags);
