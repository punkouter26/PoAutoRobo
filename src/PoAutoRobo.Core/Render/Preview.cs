namespace PoAutoRobo.Core.Render;

/// <summary>A quick low-resolution render for the preview player, plus the word timings its caption overlay draws from.</summary>
/// <param name="Marks">Where each clip starts, with its narration file, for the waveform under the player.</param>
public sealed record Preview(string VideoPath, IReadOnlyList<CaptionSegment> Segments, IReadOnlyList<ClipMark> Marks);

/// <summary>One clip's place on the finished timeline.</summary>
public sealed record ClipMark(string Title, TimeSpan Start, string AudioPath);

/// <summary>Where a render has got to: what it is doing in plain words, and how far through the whole job it is (0 to 1).</summary>
/// <param name="Clips">How far each clip's picture has got (0 to 1), in running order; null outside the drawing stage.</param>
public sealed record RenderProgress(string Activity, double Fraction, IReadOnlyList<double>? Clips = null);
