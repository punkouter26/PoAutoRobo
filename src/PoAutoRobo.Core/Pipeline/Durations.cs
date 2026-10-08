using PoAutoRobo.Core.Models;

namespace PoAutoRobo.Core.Pipeline;

public static class Durations
{
    // Estimate only: real audio duration replaces it once a clip is narrated. Tune here if the host voice runs faster or slower.
    public const int WordsPerMinute = 165;
    public static readonly TimeSpan Min = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(60);

    public static string[] SplitWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static int WordCount(string text) => SplitWords(text).Length;

    public static TimeSpan Estimate(string text) => TimeSpan.FromMinutes((double)WordCount(text) / WordsPerMinute);

    public static bool InRange(TimeSpan duration) => duration >= Min && duration <= Max;

    public static int TargetWords(TimeSpan duration) => (int)Math.Round(duration.TotalMinutes * WordsPerMinute);

    public static IEnumerable<(Guid ClipId, Tier Tier)> OutOfRange(Episode episode) =>
        from clip in episode.Clips
        from script in clip.Scripts.OrderBy(s => s.Key)
        where !InRange(Estimate(script.Value.Dialogue))
        select (clip.Id, script.Key);
}
