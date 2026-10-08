namespace PoAutoRobo.Core.Services;

/// <param name="ReferencePath">Picture whose character must be kept, normally the host's character sheet; null for none.</param>
public sealed record ImageRequest(string Prompt, string? ReferencePath, string Size = "1536x1024");

public interface IImageGen
{
    /// <summary>Makes one picture and saves it as a PNG at <paramref name="outputPath"/>.</summary>
    /// <exception cref="InvalidOperationException">The service refused or failed; the message is fit to show the user.</exception>
    Task GenerateAsync(ImageRequest request, string outputPath, CancellationToken ct);
}
