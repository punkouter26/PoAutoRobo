namespace PoAutoRobo.Core.Library;

/// <summary>What the owner taught the robot in one episode. The script is written around it.</summary>
/// <param name="Taught">What was taught, in the owner's words.</param>
/// <param name="WentWrong">What went wrong on the way; empty when nothing is said.</param>
/// <param name="Footage">The owner's own clips of the robot, each placed in one clip of the episode.</param>
public sealed record Lesson(string Taught, string WentWrong, BeatStructure Structure, IReadOnlyList<FootageNote> Footage)
{
    /// <summary>A lesson told as a story, with nothing gone wrong and no footage.</summary>
    public static Lesson Of(string taught) => new(taught, "", BeatStructure.Story, []);

    /// <summary>The lesson as one piece of text: what is saved as the episode's topic and what the model reads.</summary>
    public string Topic => WentWrong.Trim().Length > 0 ? $"{Taught.Trim()}\n\nWhat went wrong: {WentWrong.Trim()}" : Taught.Trim();

    /// <summary>The series so far, a line to each episode ("#3 · Standing up: …"), so this one can build on them.</summary>
    public IReadOnlyList<string> Earlier { get; init; } = [];
}

/// <param name="Path">The file as the owner picked it. It is copied into the episode once the script is written.</param>
/// <param name="Note">A line on what it shows, which tells the script where it belongs.</param>
/// <param name="OwnSound">It plays with its own sound; otherwise it is narrated over.</param>
public sealed record FootageNote(string Path, string Note, bool OwnSound, TimeSpan Length)
{
    /// <summary>What a model saw happen in the film; null when it was not watched.</summary>
    public FootageSeen? Seen { get; init; }

    /// <summary>What is said in the film, with when each word is said; null when it was not listened to.</summary>
    public IReadOnlyList<WordTiming>? Heard { get; init; }
}
