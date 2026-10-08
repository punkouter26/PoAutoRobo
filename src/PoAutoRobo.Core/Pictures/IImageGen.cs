namespace PoAutoRobo.Core.Pictures;

/// <param name="ReferencePath">Picture whose character must be kept, normally the host's character sheet; null for none.</param>
/// <param name="Quality">"low", "medium" or "high". Cost and time rise steeply with each step.</param>
public sealed record ImageRequest(string Prompt, string? ReferencePath, string Size = "1536x1024", string Quality = "medium");

public interface IImageGen
{
    /// <summary>Makes one picture and saves it as a PNG at <paramref name="outputPath"/>.</summary>
    /// <exception cref="InvalidOperationException">The service refused or failed; the message is fit to show the user.</exception>
    Task GenerateAsync(ImageRequest request, string outputPath, CancellationToken ct);
}
