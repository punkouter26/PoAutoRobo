namespace PoAutoRobo.Core.Models;

public enum CaptionPreset { KaraokeHighlight, TwoLineBlock, CleanSubtitle, ComicBanner }

/// <param name="FontSize">In pixels on a 1080-line frame; scales with the export resolution.</param>
/// <param name="AccentColor">#RRGGBB.</param>
public sealed record CaptionStyle(
    CaptionPreset Preset = CaptionPreset.KaraokeHighlight,
    int FontSize = 64,
    string AccentColor = "#FFD400",
    int StrokeWidth = 4);
