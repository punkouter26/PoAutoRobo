using System.Globalization;
using System.Text;

namespace PoAutoRobo.Core.Render;

/// <summary>The text files written beside a finished video so it is ready to upload.</summary>
public static class PublishPack
{
    /// <summary>One line per clip, "0:00 Title", in the form video sites read as chapters.</summary>
    public static string Chapters(IEnumerable<(TimeSpan Start, string Title)> clips) =>
        string.Concat(clips.Select(c => $"{(int)c.Start.TotalMinutes}:{c.Start.Seconds:00} {c.Title}\n"));

    /// <summary>Title choices, description, tags and hashtags, laid out to copy into an upload form.</summary>
    public static string Description(PublishNotes notes) =>
        $"TITLES\n{string.Join('\n', notes.Titles)}\n\nDESCRIPTION\n{notes.Description}\n\n{string.Join(' ', notes.Hashtags)}\n\nTAGS\n{string.Join(", ", notes.Tags)}\n";

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

/// <summary>What to paste into the upload form beside the finished video.</summary>
public sealed record PublishNotes(IReadOnlyList<string> Titles, string Description, IReadOnlyList<string> Tags, IReadOnlyList<string> Hashtags)
{
    /// <summary>
    /// Starts every title with the series' name and the episode's number ("Teaching the R1 #7: …"), so the uploads
    /// read as one series. A title that already starts with the name is left as it is, and so are all of them when
    /// the series has no name yet.
    /// </summary>
    public PublishNotes InSeries(string channel, int number)
    {
        if (channel.Trim() is not { Length: > 0 } name) return this;
        var prefix = number > 0 ? $"{name} #{number}: " : $"{name}: ";
        return this with { Titles = [.. Titles.Select(t => t.StartsWith(name, StringComparison.OrdinalIgnoreCase) ? t : prefix + t)] };
    }
}
