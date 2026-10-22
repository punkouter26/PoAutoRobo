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
}
