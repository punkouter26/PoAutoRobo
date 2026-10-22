namespace PoAutoRobo.Core.Library;

/// <summary>
/// The shapes an episode can take. Every episode of the series follows one, so each lesson is told the same way.
/// New structures go on the end: saved episodes name theirs.
/// </summary>
public enum BeatStructure { Story, Tutorial, HookFirst }

/// <summary>One section of an episode, shown on screen as a strap when it starts.</summary>
/// <param name="ForFootage">Real footage of the robot belongs here, more than pictures made for the episode.</param>
public sealed record Beat(string Name, bool ForFootage);

public static class BeatPlan
{
    /// <summary>The most clips one beat is given, so no section swallows the episode.</summary>
    public const int MaxClipsPerBeat = 3;

    /// <summary>The beats of a structure, in the order they are told.</summary>
    public static IReadOnlyList<Beat> Beats(BeatStructure structure) => structure switch
    {
        BeatStructure.Tutorial => [new("Concept", false), new("Setup", true), new("Demo", true), new("Recap", false)],
        BeatStructure.HookFirst => [new("Hook", true), new("Explain", false), new("Demo", true), new("Next time", false)],
        _ => [new("Goal", false), new("Try", true), new("Fail", true), new("Fix", false), new("Result", true), new("Recap", false)],
    };

    /// <summary>
    /// Holds a written script to its structure, whatever the model did: every clip in one of its beats and the beats
    /// in order, each footage file in exactly one clip, and no more than <paramref name="maxClips"/> clips unless the
    /// owner's footage needs them. A clip in a beat the structure does not have joins the beat before it.
    /// </summary>
    public static Episode Repair(Episode episode, IReadOnlyList<FootageNote> footage, int maxClips)
    {
        var beats = Beats(episode.Structure);
        var names = beats.Select(b => b.Name).ToList();
        var current = names[0];
        var clips = episode.Clips
            .Select(c => c with { Beat = current = c.Beat is { } beat && names.Contains(beat) ? beat : current })
            .OrderBy(c => names.IndexOf(c.Beat!)) // stable: clips keep their order within a beat
            .ToList();

        // A file used twice stays where footage belongs, else in its first clip; the other clip goes back to a title card.
        var keepers = clips.Where(c => c.Visual.UserVideoPath is not null)
            .GroupBy(c => c.Visual.UserVideoPath!, StringComparer.OrdinalIgnoreCase)
            .Select(same => same.OrderBy(c => beats[names.IndexOf(c.Beat!)].ForFootage ? 0 : 1).First().Id)
            .ToHashSet();
        // The one that stays takes the file's sound choice from the owner's note, whatever the clip said before.
        clips = [.. clips.Select(c => c.Visual.UserVideoPath is not { } path ? c
            : !keepers.Contains(c.Id) ? EpisodeEditor.Narratable(c with { Visual = new VisualSpec(VisualKind.TitleCard), OwnSound = false })
            : footage.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)) is { } note ? WithFootage(c, note) : c)];

        // A file left out gets a clip of its own, at the end of the footage beat that has the least footage so far.
        foreach (var note in footage.Where(n => !clips.Any(c => string.Equals(c.Visual.UserVideoPath, n.Path, StringComparison.OrdinalIgnoreCase))))
        {
            var beat = beats.Where(b => b.ForFootage).MinBy(b => clips.Count(c => c.Beat == b.Name && c.Visual.UserVideoPath is not null)) ?? beats[^1];
            var at = clips.FindLastIndex(c => names.IndexOf(c.Beat!) <= names.IndexOf(beat.Name)) + 1;
            var blank = new Clip(Guid.NewGuid(), Title(note), Tier.B, new Dictionary<Tier, TierScript> { [Tier.B] = new(note.Note, note.Note, "none") },
                new VisualSpec(VisualKind.TitleCard), HostVisible: false)
            { Beat = beat.Name };
            clips.Insert(at, WithFootage(blank, note));
        }

        // Too long: clips without footage go from the end. The owner's footage is never what is cut.
        while (clips.Count > maxClips && clips.FindLastIndex(c => c.Visual.UserVideoPath is null) is >= 0 and var spare)
            clips.RemoveAt(spare);
        return episode with { Clips = clips };
    }

    /// <summary>
    /// Puts one of the owner's files in a clip. A file that keeps its own sound has nothing narrated over it, so the
    /// clip's words are cleared: the owner types what is said, if they want it captioned. The mascot stays out of real footage.
    /// </summary>
    public static Clip WithFootage(Clip clip, FootageNote note) => clip with
    {
        Visual = new VisualSpec(VisualKind.UserVideo, KindLocked: true, UserVideoPath: note.Path),
        OwnSound = note.OwnSound,
        HostVisible = false,
        Scripts = note.OwnSound ? clip.Scripts.ToDictionary(s => s.Key, s => s.Value with { Dialogue = "" }) : clip.Scripts,
    };

    private static string Title(FootageNote note) =>
        note.Note.Trim() is { Length: > 0 } words ? words.Length > 40 ? words[..40].TrimEnd() + "…" : words : "Your footage";
}
