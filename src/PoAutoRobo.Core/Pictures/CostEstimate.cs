namespace PoAutoRobo.Core.Pictures;

/// <summary>What a "generate all" run would make, shown to the user before anything is paid for.</summary>
/// <param name="Dollars">Null when the model's price is not known; the app then shows counts only instead of guessing.</param>
/// <param name="Videos">AI videos among them. Animations and stock photos are in <paramref name="ClipIds"/> but cost no picture.</param>
public sealed record CostEstimate(IReadOnlyList<Guid> ClipIds, int Pictures, decimal? Dollars, int Videos = 0)
{
    /// <summary>Approximate list price of one eight-second 720p video from Sora 2, in US dollars (checked October 2026).</summary>
    public const decimal VideoPrice = 0.80m;

    /// <summary>
    /// Approximate list price of one 1536×1024 medium-quality picture from gpt-image-1-mini, in US dollars (checked
    /// October 2026). An estimate for the confirmation dialog only; the Azure bill is the real figure.
    /// </summary>
    public const decimal MiniPicturePrice = 0.015m;

    /// <summary>The same picture at low quality, for drafts. Approximate, like the price above.</summary>
    public const decimal MiniDraftPrice = 0.006m;

    public bool NothingToDo => ClipIds.Count == 0;

    public string Summary
    {
        get
        {
            if (NothingToDo) return "Every clip already has its picture.";
            var count = string.Join(" and ", new[]
            {
                Pictures == 1 ? "1 picture" : Pictures > 1 ? $"{Pictures} pictures" : "",
                Videos == 1 ? "1 AI video" : Videos > 1 ? $"{Videos} AI videos" : "",
            }.Where(part => part.Length > 0));
            if (count.Length == 0) return "Animations and stock photos only, which cost no picture.";
            return Dollars is { } dollars
                ? $"{count}, about {SpendLog.Money(dollars)}."
                : $"{count}. The cost depends on your Azure pricing.";
        }
    }

    public static CostEstimate For(Episode episode, string imageModel, Quality quality = Quality.Medium)
    {
        var clips = episode.Clips.Where(NeedsPicture).ToList();
        var pictures = clips.Sum(c => Visuals.PictureCount(c.Visual.Kind));
        var videos = clips.Count(c => c.Visual.Kind == VisualKind.AiVideo);
        // No pictures means no unknown price, whatever the model is.
        return new CostEstimate([.. clips.Select(c => c.Id)], pictures, (pictures == 0 ? 0 : pictures * PriceOf(imageModel, quality)) + videos * VideoPrice, videos);
    }

    /// <summary>Price of one picture from this model, or null when it is not known.</summary>
    public static decimal? PriceOf(string imageModel, Quality quality = Quality.Medium) => (imageModel, quality) switch
    {
        ("gpt-image-1-mini", Quality.Medium) => MiniPicturePrice,
        ("gpt-image-1-mini", Quality.Low) => MiniDraftPrice,
        _ => null,
    };

    /// <summary>Generated kinds with no picture yet, or one the dialogue has since moved away from.</summary>
    public static bool NeedsPicture(Clip clip) =>
        Visuals.IsGenerated(clip.Visual.Kind)
        && (clip.Visual.Stale || clip.Visual.MediaPaths is not { Count: > 0 });
}
