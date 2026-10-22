namespace PoAutoRobo.Core.Library;

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
