namespace PoAutoRobo.Core.Render;

/// <summary>One moment of a teaser: a stretch of one clip, timed from that clip's start.</summary>
public sealed record TeaserMoment(Guid ClipId, TimeSpan Start, TimeSpan Length);

/// <summary>A ten-second upright cut for other video sites: a hook line over one to three moments of the episode.</summary>
public sealed record Teaser(string Hook, IReadOnlyList<TeaserMoment> Moments)
{
    public TimeSpan Length => TimeSpan.FromTicks(Moments.Sum(m => m.Length.Ticks));
}

/// <summary>The rules every teaser keeps, and teasers made by those rules alone when no model is asked.</summary>
public static class Teasers
{
    public static readonly TimeSpan Target = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(0.5);

    /// <summary>The shortest moment worth cutting to: anything less is a flash the eye cannot read.</summary>
    public static readonly TimeSpan MinMoment = TimeSpan.FromSeconds(1);

    public const int MaxMoments = 3;
    public const int MaxHookWords = 8;
    public const int MaxTeasers = 3;

    /// <summary>
    /// Holds suggested teasers to the rules: moments only in clips that are there and inside their length, at most
    /// three of them adding up to ten seconds, and a hook of at most eight words. A teaser is trimmed or stretched
    /// within its own clips to fit, keeping each moment as near as it can to where it was asked for; one that cannot
    /// be made to fit is dropped, as is one that repeats another.
    /// </summary>
    /// <param name="lengths">How long each clip runs in the finished episode.</param>
    public static IReadOnlyList<Teaser> Fit(IEnumerable<Teaser> suggested, IReadOnlyDictionary<Guid, TimeSpan> lengths)
    {
        var kept = new List<Teaser>();
        foreach (var teaser in suggested)
        {
            var moments = teaser.Moments.Where(m => lengths.ContainsKey(m.ClipId)).Take(MaxMoments)
                .Select(m => m with { Length = Clamp(m.Length, TimeSpan.Zero, lengths[m.ClipId]) }).ToList();
            var total = TimeSpan.FromTicks(moments.Sum(m => m.Length.Ticks));

            // Too long: the later moments give up time first. Too short: the later moments take more of their clip.
            for (var i = moments.Count - 1; i >= 0 && total > Target + Tolerance; i--)
            {
                var cut = Min(total - Target, moments[i].Length);
                moments[i] = moments[i] with { Length = moments[i].Length - cut };
                total -= cut;
            }
            for (var i = moments.Count - 1; i >= 0 && total < Target - Tolerance; i--)
            {
                var grow = Min(Target - total, lengths[moments[i].ClipId] - moments[i].Length);
                moments[i] = moments[i] with { Length = moments[i].Length + grow };
                total += grow;
            }
            // Then each is placed as near as it fits to where it was asked for.
            moments = [.. moments.Where(m => m.Length >= MinMoment).Select(m => m with { Start = Clamp(m.Start, TimeSpan.Zero, lengths[m.ClipId] - m.Length) })];

            var fitted = new Teaser(Hook(teaser.Hook), moments);
            if (moments.Count > 0 && (fitted.Length - Target).Duration() <= Tolerance && !kept.Any(k => k.Moments.SequenceEqual(moments)))
                kept.Add(fitted);
            if (kept.Count == MaxTeasers) break;
        }
        return kept;
    }

    /// <summary>
    /// Three teasers by rule, for when no model is asked: the opening of the first footage, the result (or the demo),
    /// and the last ten seconds of the episode.
    /// </summary>
    public static IReadOnlyList<Teaser> ByRule(Episode episode, IReadOnlyDictionary<Guid, TimeSpan> lengths)
    {
        var clips = episode.Clips.Where(c => lengths.ContainsKey(c.Id)).ToList();
        if (clips.Count == 0) return [];
        var footageBeats = BeatPlan.Beats(episode.Structure).Where(b => b.ForFootage).Select(b => b.Name).ToHashSet();
        var opening = Math.Max(0, clips.FindIndex(c => c.Beat is { } beat && footageBeats.Contains(beat)));
        var payoff = clips.FindLastIndex(c => c.Beat is "Result" or "Demo") is >= 0 and var found ? found : clips.Count / 2;

        return Fit(
        [
            new(episode.Title, Forward(opening)),
            new("Watch it work", Forward(payoff)),
            new("How it turned out", Ending()),
        ], lengths);

        // Ten seconds from the start of one clip, running on into the ones after it; near the end of the episode, the
        // close of the clips before it make up the rest.
        List<TeaserMoment> Forward(int from)
        {
            var moments = new List<TeaserMoment>();
            var left = Target;
            for (var i = from; i < clips.Count && moments.Count < MaxMoments && left >= MinMoment; i++)
            {
                var take = Min(left, lengths[clips[i].Id]);
                moments.Add(new TeaserMoment(clips[i].Id, TimeSpan.Zero, take));
                left -= take;
            }
            for (var i = from - 1; i >= 0 && moments.Count < MaxMoments && left >= MinMoment; i--)
            {
                var length = lengths[clips[i].Id];
                var take = Min(left, length);
                moments.Insert(0, new TeaserMoment(clips[i].Id, length - take, take));
                left -= take;
            }
            return moments;
        }

        // The last ten seconds of the episode, gathered from the end backwards.
        List<TeaserMoment> Ending()
        {
            var moments = new List<TeaserMoment>();
            var left = Target;
            for (var i = clips.Count - 1; i >= 0 && moments.Count < MaxMoments && left >= MinMoment; i--)
            {
                var length = lengths[clips[i].Id];
                var take = Min(left, length);
                moments.Insert(0, new TeaserMoment(clips[i].Id, length - take, take));
                left -= take;
            }
            return moments;
        }
    }

    // A hook is read at a glance: no more than eight words.
    private static string Hook(string hook) => string.Join(' ', Durations.SplitWords(hook).Take(MaxHookWords));

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan low, TimeSpan high) => value < low ? low : value > high ? high : value;
}
