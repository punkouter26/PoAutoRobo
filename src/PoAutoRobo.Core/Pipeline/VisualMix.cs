using PoAutoRobo.Core.Models;

namespace PoAutoRobo.Core.Pipeline;

public sealed record MixPercentages(int Still, int MultiPanel, int AiVideo, int TitleCard)
{
    public static readonly MixPercentages Default = new(50, 20, 20, 10);
}

public static class VisualMix
{
    /// <summary>Largest-remainder split of <paramref name="clipCount"/> clips; ties go to the earlier kind.</summary>
    public static IReadOnlyDictionary<VisualKind, int> Counts(MixPercentages mix, int clipCount)
    {
        (VisualKind Kind, int Percent)[] shares =
        [
            (VisualKind.Still, mix.Still), (VisualKind.MultiPanel, mix.MultiPanel),
            (VisualKind.AiVideo, mix.AiVideo), (VisualKind.TitleCard, mix.TitleCard),
        ];
        if (shares.Any(s => s.Percent < 0) || shares.Sum(s => s.Percent) != 100)
            throw new ArgumentException("Percentages must be zero or more and add up to 100.", nameof(mix));

        var counts = shares.ToDictionary(s => s.Kind, s => s.Percent * clipCount / 100);
        var spare = clipCount - counts.Values.Sum();
        foreach (var (kind, _) in shares.OrderByDescending(s => s.Percent * clipCount % 100).Take(spare)) // stable sort keeps listed order on ties
            counts[kind]++;
        return counts;
    }

    /// <summary>Deals kinds to clips using <see cref="Episode.MixSeed"/>. User footage and hand-picked kinds are untouched.</summary>
    public static Episode Assign(Episode episode, MixPercentages mix)
    {
        static bool Eligible(Clip c) => c.Visual is { KindLocked: false, Kind: not VisualKind.UserVideo };

        var deck = Counts(mix, episode.Clips.Count(Eligible))
            .SelectMany(c => Enumerable.Repeat(c.Key, c.Value))
            .ToArray();
        new Random(episode.MixSeed).Shuffle(deck);

        var next = 0;
        return episode with
        {
            Clips = [.. episode.Clips.Select(c => Eligible(c) ? c with { Visual = c.Visual with { Kind = deck[next++] } } : c)],
        };
    }
}
