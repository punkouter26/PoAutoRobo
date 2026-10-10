namespace PoAutoRobo.Core.Pictures;

/// <summary>How good a picture is asked to be. Cost and time rise steeply with each step.</summary>
public enum Quality { Low, Medium, High }

/// <param name="ReferencePath">Picture whose character must be kept, normally the host's character sheet; null for none.</param>
/// <param name="Transparent">Nothing behind the subject, so the picture can be laid over another.</param>
public sealed record ImageRequest(string Prompt, string? ReferencePath, string Size = "1536x1024", Quality Quality = Quality.Medium, bool Transparent = false);

/// <summary>The picture service would not draw what was described. Another picture of the same idea may be drawn where this one was not.</summary>
/// <param name="flagged">What it objected to and how strongly, for example "violence: medium"; null when it did not say.</param>
/// <param name="said">The service's own words, when it gave any.</param>
public sealed class PictureDeclinedException(string? flagged, string? said = null)
    : InvalidOperationException($"The picture service would not draw this{(flagged is null ? "" : $" ({flagged})")}. {(said is null ? "" : said + " ")}Describe the picture another way, or choose another picture type for the clip.")
{
    public string? Flagged { get; } = flagged;
}

/// <summary>Makes one picture and saves it as a PNG at <paramref name="outputPath"/>.</summary>
/// <exception cref="InvalidOperationException">The service refused or failed; the message is fit to show the user.</exception>
public delegate Task ImageMaker(ImageRequest request, string outputPath, CancellationToken ct);
