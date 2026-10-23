
namespace PoAutoRobo.Core.Script;

public static class Durations
{
    /// <summary>How fast a voice is taken to speak until it has been heard. Real audio replaces every estimate once a clip is narrated.</summary>
    public const double DefaultWordsPerMinute = 165;

    /// <summary>
    /// How fast the voice speaks, learned from its recordings (<see cref="Learned"/>), so estimates, scene lengths and
    /// the words asked for to fit a film come out close the first time.
    /// </summary>
    public static double WordsPerMinute { get; set; } = DefaultWordsPerMinute;

    /// <summary>Lines shorter than this say too little about the pace: a pause weighs too much in them.</summary>
    private const int FewestWordsToLearnFrom = 12;

    /// <summary>
    /// The pace once one recording at the normal pace is taken into account. Each moves it a fifth of the way towards
    /// its own, so one odd line cannot swing it; and it stays within what any voice speaks at.
    /// </summary>
    /// <returns>Null when the line is too short to say anything about the pace.</returns>
    public static double? Learned(double pace, string text, TimeSpan spoken)
    {
        var words = WordCount(text);
        if (words < FewestWordsToLearnFrom || spoken <= TimeSpan.Zero) return null;
        return Math.Clamp(0.8 * pace + 0.2 * (words / spoken.TotalMinutes), 110, 220);
    }
    public static readonly TimeSpan Min = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(60);

    public static string[] SplitWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// How much two lines have in common: the share (0 to 1) of the longer line's content words that the other also uses.
    /// Short words and punctuation are ignored.
    /// </summary>
    public static double SharedWords(string first, string second)
    {
        static HashSet<string> Content(string text) =>
            [.. SplitWords(text).Select(w => w.Trim('.', ',', '!', '?', ';', ':').ToLowerInvariant()).Where(w => w.Length > 3)];

        var (a, b) = (Content(first), Content(second));
        var most = Math.Max(a.Count, b.Count);
        return most == 0 ? 1 : (double)a.Intersect(b).Count() / most;
    }

    public static int WordCount(string text) => SplitWords(text).Length;

    public static TimeSpan Estimate(string text) => TimeSpan.FromMinutes((double)WordCount(text) / WordsPerMinute);

    /// <summary>About how many words the voice says in <paramref name="length"/>.</summary>
    public static int WordsFor(TimeSpan length) => Math.Max(1, (int)Math.Round(length.TotalMinutes * WordsPerMinute));

    /// <summary>The running time the series aims for, without the intro and outro. Outside it is flagged, never blocked.</summary>
    public static readonly TimeSpan MinEpisode = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan MaxEpisode = TimeSpan.FromMinutes(5);

    /// <summary>Estimated running time of the episode as currently set (each clip's active tier).</summary>
    public static TimeSpan Total(Episode episode) =>
        TimeSpan.FromTicks(episode.Clips.Sum(c => Estimate(c.Active.Dialogue).Ticks));

    /// <summary>In plain words, how the running time misses the series' three to five minutes; null when it does not.</summary>
    public static string? RuntimeWarning(TimeSpan total) =>
        total < MinEpisode ? "shorter than the 3 minute target" : total > MaxEpisode ? "longer than the 5 minute target" : null;

    /// <summary>Plain-words warning when a clip's active dialogue runs outside 15 to 60 seconds; null when it is fine.</summary>
    public static string? Warning(Clip clip) => Warning(clip.Active.Dialogue);

    public static string? Warning(string dialogue)
    {
        var duration = Estimate(dialogue);
        return duration < Min ? "Shorter than 15 seconds" : duration > Max ? "Longer than 60 seconds" : null;
    }
}
