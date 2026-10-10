
namespace PoAutoRobo.Core.Script;

/// <summary>Offline stand-in used when no Azure OpenAI credentials are configured.</summary>
public sealed class MockScriptWriter : IScriptWriter
{
    private static readonly (string Title, string Pose)[] Outline =
    [
        ("Meet the R1", "waving at the camera"),
        ("Why balance is hard", "wobbling on one foot with arms out"),
        ("Joints and actuators", "holding a virtual torque wrench"),
        ("Sensing the body", "tapping the side of its head"),
        ("The control loop", "pointing at a looping arrow diagram"),
        ("Simulation first", "standing beside a simulation viewport"),
        ("Building the scene", "placing blocks on a virtual floor"),
        ("Observations", "holding up a clipboard of numbers"),
        ("Actions", "flexing one knee"),
        ("Reward design", "pointing at a whiteboard of reward terms"),
        ("Training runs", "watching a row of tiny robots"),
        ("Reading the curves", "tracing a rising graph with one finger"),
        ("Domain randomization", "juggling floor tiles of different textures"),
        ("Sim to real", "stepping out of a screen onto a lab floor"),
        ("First real test", "standing confidently with a safety harness"),
        ("What to try next", "giving a thumbs up"),
    ];

    private const string Filler = "That is the idea in a nutshell, and it is worth saying again in a slightly different way so it really sticks.";

    public Task<TierScript> WriteTierAsync(string topic, Subject subject, Clip clip, Tier tier, CancellationToken ct) =>
        Task.FromResult(Script(topic, clip.Title, clip.Active.Pose, tier));

    public Task<Episode> WriteEpisodeAsync(string topic, Subject subject, IReadOnlyList<GroundingSnippet> grounding, EpisodeLength length, CancellationToken ct, IProgress<IReadOnlyList<string>>? clipsWritten = null)
    {
        var clips = Outline.Take(Math.Clamp(Outline.Length, length.MinClips, length.MaxClips)).Select(o => new Clip(
            Guid.NewGuid(), o.Title, Tier.B,
            Enum.GetValues<Tier>().ToDictionary(t => t, t => Script(topic, o.Title, o.Pose, t)),
            new VisualSpec(VisualKind.TitleCard), HostVisible: subject != Subject.Essay)).ToList();
        return Task.FromResult(new Episode(topic, topic, clips, MixSeed: Random.Shared.Next()) { Subject = subject });
    }

    private static TierScript Script(string topic, string title, string pose, Tier tier)
    {
        var subject = title.ToLowerInvariant();
        var dialogue = tier switch
        {
            Tier.A => $"Okay, {subject}! Picture yourself standing on a moving bus without holding on. Your body keeps making tiny corrections so you stay upright, and you never even think about it. The R1 has to learn that same trick from scratch, and this part of {topic} is where it starts to click.",
            Tier.B => $"Next up, {subject}. In practice this is where you set up the workflow: pick the task, check the observation and action spaces, and decide which reward terms matter most. Small changes here ripple through training, so keep one variable moving at a time, log everything, and compare against your last good run before you trust the result for {topic}.",
            _ => $"Now the deep end of {subject}. The policy outputs joint position targets that a low-level proportional-derivative loop tracks at a much higher rate than the policy itself runs. Frame transforms take base-frame velocities and gravity into the observation vector, and torque limits clip what the actuators can deliver. Every one of those details shapes how {topic} behaves once the controller leaves simulation and meets real hardware.",
        };
        var look = tier switch
        {
            Tier.A => "friendly everyday analogy scene",
            Tier.B => "practical workflow diagram",
            _ => "detailed control-systems schematic",
        };
        return new TierScript(dialogue, $"Comic panel about {subject}: {look}, host {pose}.", pose);
    }

    // ponytail: word-overlap heuristic stands in for the model's judgement; the real check is AzureScriptWriter.
    public Task<bool> CoreChangedAsync(string oldDialogue, string newDialogue, CancellationToken ct) =>
        Task.FromResult(Durations.SharedWords(oldDialogue, newDialogue) < 0.5);

    public Task<PublishNotes> WritePublishNotesAsync(Episode episode, CancellationToken ct) =>
        Task.FromResult(new PublishNotes(
            [episode.Title],
            $"{episode.Title}: {string.Join(", ", episode.Clips.Select(c => c.Title))}.",
            [.. episode.Clips.Select(c => c.Title.ToLowerInvariant())],
            ["#robotics"]));

    /// <summary>What the scene shows, as words that fade in over a bar that fills: enough to exercise the renderer offline.</summary>
    public Task<IReadOnlyList<ClipNote>> ReviewAsync(Episode episode, CancellationToken ct) => Task.FromResult<IReadOnlyList<ClipNote>>([]);

    public Task<Scene> WriteSceneAsync(SceneRequest request, CancellationToken ct, Scene? failed = null, string? problem = null) => Task.FromResult(new Scene(
        $"""
        <svg viewBox="0 0 1920 1080" xmlns="http://www.w3.org/2000/svg">
          <rect width="1920" height="1080" fill="#101828"/>
          <rect id="bar" x="160" y="620" width="0" height="24" rx="12" fill="#f79009"/>
          <text id="words" x="960" y="500" text-anchor="middle" font-family="Segoe UI" font-size="96" font-weight="700" fill="#ffffff">{System.Net.WebUtility.HtmlEncode(string.Join(' ', Durations.SplitWords(request.Shows).Take(5)))}</text>
        </svg>
        """,
        """
        function render(t, duration) {
          const p = Math.min(1, Math.max(0, t / (duration * 0.85)));
          document.getElementById('bar').setAttribute('width', 1600 * p * (2 - p));
          document.getElementById('words').setAttribute('opacity', Math.min(1, t * 2));
        }
        """));

    public Task<string> RewriteToLengthAsync(string dialogue, int targetWords, CancellationToken ct)
    {
        var words = Durations.SplitWords(dialogue).ToList();
        var filler = Durations.SplitWords(Filler);
        for (var i = 0; words.Count < targetWords; i++)
            words.Add(filler[i % filler.Length]);
        return Task.FromResult(string.Join(' ', words.Take(targetWords)));
    }
}
