using System.Globalization;
using PoAutoRobo.Core.Models;

namespace PoAutoRobo.Core.Pipeline;

/// <summary>What a "generate all" run would make, shown to the user before anything is paid for.</summary>
/// <param name="Dollars">Null when the model's price is not known; the app then shows counts only instead of guessing.</param>
public sealed record CostEstimate(IReadOnlyList<Guid> ClipIds, decimal? Dollars)
{
    /// <summary>
    /// Approximate list price of one 1536×1024 medium-quality picture from gpt-image-1-mini, in US dollars (checked
    /// October 2026). An estimate for the confirmation dialog only; the Azure bill is the real figure.
    /// </summary>
    public const decimal MiniPicturePrice = 0.015m;

    public int Pictures => ClipIds.Count;

    public bool NothingToDo => Pictures == 0;

    public string Summary
    {
        get
        {
            if (NothingToDo) return "Every clip already has its picture.";
            var count = Pictures == 1 ? "1 picture" : $"{Pictures} pictures";
            return Dollars is { } dollars
                ? $"{count}, about ${dollars.ToString("0.00", CultureInfo.InvariantCulture)}."
                : $"{count}. The cost depends on your Azure pricing.";
        }
    }

    public static CostEstimate For(Episode episode, string imageModel)
    {
        var ids = episode.Clips.Where(NeedsPicture).Select(c => c.Id).ToList();
        return new CostEstimate(ids, imageModel == "gpt-image-1-mini" ? ids.Count * MiniPicturePrice : null);
    }

    /// <summary>Generated kinds with no picture yet, or one the dialogue has since moved away from.</summary>
    public static bool NeedsPicture(Clip clip) =>
        clip.Visual.Kind is VisualKind.Still or VisualKind.MultiPanel or VisualKind.AiVideo
        && (clip.Visual.Stale || clip.Visual.MediaPaths is not { Count: > 0 });
}
