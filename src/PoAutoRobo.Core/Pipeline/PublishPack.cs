using System.Globalization;
using System.Text;
using PoAutoRobo.Core.Models;

namespace PoAutoRobo.Core.Pipeline;

/// <summary>The text files written beside a finished video so it is ready to upload.</summary>
public static class PublishPack
{
    /// <summary>One line per clip, "0:00 Title", in the form video sites read as chapters.</summary>
    public static string Chapters(IEnumerable<(TimeSpan Start, string Title)> clips) =>
        string.Concat(clips.Select(c => $"{(int)c.Start.TotalMinutes}:{c.Start.Seconds:00} {c.Title}\n"));

    /// <summary>The narration as a subtitle file, one plain line at a time whatever the burned-in caption style is.</summary>
    public static string Srt(IEnumerable<CaptionSegment> segments)
    {
        var srt = new StringBuilder();
        var number = 1;
        foreach (var cue in AssCaptions.Cues(segments, new CaptionStyle(CaptionPreset.CleanSubtitle)))
            srt.Append(CultureInfo.InvariantCulture, $"{number++}\n{Time(cue.Start)} --> {Time(cue.End)}\n{string.Join(' ', cue.Words)}\n\n");
        return srt.ToString();

        static string Time(TimeSpan t) => t.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
    }
}
