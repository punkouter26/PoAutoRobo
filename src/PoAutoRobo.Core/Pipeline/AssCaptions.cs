using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Pipeline;

/// <summary>One clip's words, positioned on the episode timeline by <paramref name="Offset"/>.</summary>
public sealed record CaptionSegment(TimeSpan Offset, IReadOnlyList<WordTiming> Words);

/// <summary>Writes burned-in captions as an Advanced SubStation Alpha file, which FFmpeg renders natively.</summary>
public static partial class AssCaptions
{
    private const string White = "&H00FFFFFF";
    private const string Black = "&H00000000";

    private sealed record Look(string Font, double Scale, string Text, string Box, bool Boxed, bool Upper, int MaxWords, bool TwoLines);

    public static string Build(IEnumerable<CaptionSegment> segments, CaptionStyle style)
    {
        var accent = AssColour(style.AccentColor);
        var look = style.Preset switch
        {
            CaptionPreset.KaraokeHighlight => new Look("Segoe UI", 1.0, White, Black, Boxed: false, Upper: false, MaxWords: 5, TwoLines: false),
            CaptionPreset.TwoLineBlock => new Look("Segoe UI", 1.0, White, Black, Boxed: true, Upper: false, MaxWords: 10, TwoLines: true),
            CaptionPreset.CleanSubtitle => new Look("Segoe UI", 0.8, White, Black, Boxed: false, Upper: false, MaxWords: 8, TwoLines: false),
            _ => new Look("Comic Sans MS", 1.0, Black, accent, Boxed: true, Upper: true, MaxWords: 6, TwoLines: false),
        };
        var size = (int)Math.Round(style.FontSize * look.Scale);
        var outline = style.Preset == CaptionPreset.CleanSubtitle ? Math.Max(1, style.StrokeWidth / 2) : style.StrokeWidth;

        var ass = new StringBuilder()
            .AppendLine("[Script Info]")
            .AppendLine("ScriptType: v4.00+")
            .AppendLine("PlayResX: 1920")
            .AppendLine("PlayResY: 1080")
            .AppendLine("WrapStyle: 2")
            .AppendLine()
            .AppendLine("[V4+ Styles]")
            .AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding")
            .AppendLine($"Style: Default,{look.Font},{size},{look.Text},{look.Text},{look.Box},{look.Box},-1,0,0,0,100,100,0,0,{(look.Boxed ? 3 : 1)},{outline},0,2,80,80,90,1")
            .AppendLine()
            .AppendLine("[Events]")
            .AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

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
                        var text = string.Join(' ', words.Select((w, j) => j == i ? $"{{\\c{accent}}}{w}{{\\c{White}}}" : w));
                        Event(ass, segment.Offset + line[i].Start, segment.Offset + end, text);
                    }
                }
                else
                {
                    var split = look.TwoLines && words.Count > look.MaxWords / 2 ? (words.Count + 1) / 2 : words.Count;
                    var text = string.Join(' ', words.Take(split)) + (split < words.Count ? @"\N" + string.Join(' ', words.Skip(split)) : "");
                    Event(ass, segment.Offset + line[0].Start, segment.Offset + line[^1].Start + line[^1].Duration, text);
                }
            }
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

    private static void Event(StringBuilder ass, TimeSpan start, TimeSpan end, string text) =>
        ass.AppendLine($"Dialogue: 0,{Time(start)},{Time(end)},Default,,0,0,0,,{text}");

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
