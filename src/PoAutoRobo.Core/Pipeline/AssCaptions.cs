using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Pipeline;

/// <summary>One clip's words, positioned on the episode timeline by <paramref name="Offset"/>.</summary>
public sealed record CaptionSegment(TimeSpan Offset, IReadOnlyList<WordTiming> Words);

/// <summary>One caption as it appears on screen.</summary>
/// <param name="Highlight">Index of the word being spoken, or -1 when the preset does not light words.</param>
/// <param name="FirstLineWords">How many words sit on the first line; equals the word count for a single line.</param>
public sealed record CaptionCue(TimeSpan Start, TimeSpan End, IReadOnlyList<string> Words, int Highlight, int FirstLineWords);

/// <summary>How a preset looks. The export and the live preview both draw from this.</summary>
public sealed record CaptionLook(string Font, int FontSize, int Outline, bool Boxed, bool DarkTextOnAccent, int MaxWords, bool TwoLines, bool Upper);

/// <summary>Writes burned-in captions as an Advanced SubStation Alpha file, which FFmpeg renders natively.</summary>
public static partial class AssCaptions
{
    private const string White = "&H00FFFFFF";
    private const string Black = "&H00000000";

    public static CaptionLook LookFor(CaptionStyle style) => style.Preset switch
    {
        CaptionPreset.KaraokeHighlight => new("Segoe UI", style.FontSize, style.StrokeWidth, Boxed: false, DarkTextOnAccent: false, MaxWords: 5, TwoLines: false, Upper: false),
        CaptionPreset.TwoLineBlock => new("Segoe UI", style.FontSize, style.StrokeWidth, Boxed: true, DarkTextOnAccent: false, MaxWords: 10, TwoLines: true, Upper: false),
        CaptionPreset.CleanSubtitle => new("Segoe UI", (int)Math.Round(style.FontSize * 0.8), Math.Max(1, style.StrokeWidth / 2), Boxed: false, DarkTextOnAccent: false, MaxWords: 8, TwoLines: false, Upper: false),
        _ => new("Comic Sans MS", style.FontSize, style.StrokeWidth, Boxed: true, DarkTextOnAccent: true, MaxWords: 6, TwoLines: false, Upper: true),
    };

    /// <summary>Every caption in order. Captions never span two clips.</summary>
    public static IReadOnlyList<CaptionCue> Cues(IEnumerable<CaptionSegment> segments, CaptionStyle style)
    {
        var look = LookFor(style);
        var cues = new List<CaptionCue>();
        foreach (var segment in segments)
        {
            foreach (var line in Lines(segment.Words, look.MaxWords))
            {
                var words = line.Select(w => Clean(look.Upper ? w.Text.ToUpperInvariant() : w.Text)).ToList();
                if (style.Preset == CaptionPreset.KaraokeHighlight)
                {
                    for (var i = 0; i < line.Count; i++)
                    {
                        var end = i + 1 < line.Count ? line[i + 1].Start : line[i].Start + line[i].Duration;
                        cues.Add(new CaptionCue(segment.Offset + line[i].Start, segment.Offset + end, words, i, words.Count));
                    }
                }
                else
                {
                    var firstLine = look.TwoLines && words.Count > look.MaxWords / 2 ? (words.Count + 1) / 2 : words.Count;
                    cues.Add(new CaptionCue(segment.Offset + line[0].Start, segment.Offset + line[^1].Start + line[^1].Duration, words, -1, firstLine));
                }
            }
        }
        return cues;
    }

    /// <summary>The caption on screen at <paramref name="position"/>, or null between captions.</summary>
    public static CaptionCue? CueAt(IReadOnlyList<CaptionCue> cues, TimeSpan position) =>
        cues.FirstOrDefault(c => c.Start <= position && position < c.End);

    public static string Build(IEnumerable<CaptionSegment> segments, CaptionStyle style)
    {
        var accent = AssColour(style.AccentColor);
        var look = LookFor(style);
        var (text, box) = look.DarkTextOnAccent ? (Black, accent) : (White, Black);

        var ass = new StringBuilder()
            .AppendLine("[Script Info]")
            .AppendLine("ScriptType: v4.00+")
            .AppendLine("PlayResX: 1920")
            .AppendLine("PlayResY: 1080")
            .AppendLine("WrapStyle: 2")
            .AppendLine()
            .AppendLine("[V4+ Styles]")
            .AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding")
            .AppendLine($"Style: Default,{look.Font},{look.FontSize},{text},{text},{box},{box},-1,0,0,0,100,100,0,0,{(look.Boxed ? 3 : 1)},{look.Outline},0,2,80,80,90,1")
            .AppendLine()
            .AppendLine("[Events]")
            .AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        foreach (var cue in Cues(segments, style))
        {
            var words = cue.Words.Select((w, i) => i == cue.Highlight ? $"{{\\c{accent}}}{w}{{\\c{White}}}" : w).ToList();
            var line = string.Join(' ', words.Take(cue.FirstLineWords))
                + (cue.FirstLineWords < words.Count ? @"\N" + string.Join(' ', words.Skip(cue.FirstLineWords)) : "");
            ass.AppendLine($"Dialogue: 0,{Time(cue.Start)},{Time(cue.End)},Default,,0,0,0,,{line}");
        }
        return ass.ToString();
    }

    /// <summary>Breaks at the word limit or the end of a sentence, whichever comes first.</summary>
    private static IEnumerable<List<WordTiming>> Lines(IReadOnlyList<WordTiming> words, int maxWords)
    {
        var line = new List<WordTiming>();
        foreach (var word in words)
        {
            line.Add(word);
            if (line.Count == maxWords || word.Text[^1] is '.' or '!' or '?')
            {
                yield return line;
                line = [];
            }
        }
        if (line.Count > 0)
            yield return line;
    }

    private static string Time(TimeSpan t) => t.ToString(@"h\:mm\:ss\.ff", CultureInfo.InvariantCulture);

    // Braces and backslashes are ASS override syntax; dialogue must never be able to inject them.
    private static string Clean(string word) => word.Replace("{", "").Replace("}", "").Replace("\\", "");

    private static string AssColour(string hex)
    {
        if (!HexColour().IsMatch(hex))
            throw new ArgumentException($"'{hex}' is not a #RRGGBB colour.", nameof(hex));
        return $"&H00{hex[5..7]}{hex[3..5]}{hex[1..3]}".ToUpperInvariant();
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColour();
}
