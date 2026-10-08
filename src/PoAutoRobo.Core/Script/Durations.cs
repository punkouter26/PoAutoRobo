
namespace PoAutoRobo.Core.Script;

public static class Durations
{
    // Estimate only: real audio duration replaces it once a clip is narrated. Tune here if the host voice runs faster or slower.
    public const int WordsPerMinute = 165;
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

    /// <summary>Shortest episode the format aims for. Shorter ones are flagged, never blocked.</summary>
    public static readonly TimeSpan MinEpisode = TimeSpan.FromMinutes(3);

    /// <summary>Estimated running time of the episode as currently set (each clip's active tier).</summary>
    public static TimeSpan Total(Episode episode) =>
        TimeSpan.FromTicks(episode.Clips.Sum(c => Estimate(c.Active.Dialogue).Ticks));

    public static bool IsShort(TimeSpan total) => total < MinEpisode;

    /// <summary>Plain-words warning when a clip's active dialogue runs outside 15 to 60 seconds; null when it is fine.</summary>
    public static string? Warning(Clip clip) => Warning(clip.Active.Dialogue);

    public static string? Warning(string dialogue)
    {
        var duration = Estimate(dialogue);
        return duration < Min ? "Shorter than 15 seconds" : duration > Max ? "Longer than 60 seconds" : null;
    }
}
