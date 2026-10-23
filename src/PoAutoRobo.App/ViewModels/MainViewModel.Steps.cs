using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PoAutoRobo.App.ViewModels;

/// <summary>One thing to do on a step, as the bar under the header lists it: done, the one to do next, or still to come.</summary>
/// <param name="Optional">Worth doing but not needed to move on; a step is done when every other item is.</param>
public sealed record GuideItem(string Text, bool Done, bool Next = false, bool Optional = false)
{
    /// <summary>A tick in a circle when done, a filled dot for the one to do next, an empty ring for the rest.</summary>
    public string Glyph => Done ? "" : Next ? "" : "";

    /// <summary>Said "optional" beside it while it is not done; once done, it no longer matters that it was.</summary>
    public bool OptionalLeft => Optional && !Done;

    /// <summary>What a screen reader says, as the signs say it on screen.</summary>
    public string Spoken => $"{Text}, {(Done ? "done" : Next ? "to do next" : OptionalLeft ? "optional" : "to do")}";
}

// The five steps, and the bar under the header that says what each one asks for and what to do next.
public partial class MainViewModel
{
    // The steps, in the order the selector in the header shows them. The pages bind to the numbers.
    public const int IntroStep = 0, LessonStep = 1, ClipsStep = 2, PreviewStep = 3, PublishStep = 4;

    private static readonly string[] StepNames = ["Intro", "Lesson", "Clips", "Preview", "Publish"];

    private static readonly string[] StepHints =
    [
        "The intro every episode opens with: the channel's name, logo, mascot and sting. Set it up once; every episode then starts the same way.",
        "Say what you taught the R1 this time, add your films of it, and write the script; or open an episode you saved earlier.",
        "Shape the clips: drag them into order, edit and audition each one's words, then give each its voice and its picture.",
        "Watch the whole episode as it will play, with its captions and music, and change what needs changing.",
        "Render the master video and everything to upload with it, and the short upright teasers for other sites.",
    ];

    private int _step;

    /// <summary>
    /// Which page is showing; see the step numbers above. The pages after the lesson have nothing to show until there is
    /// an episode, so asking for one then gives the lesson; the asker is told, and shows the page really on.
    /// </summary>
    public int Step
    {
        get => _step;
        set
        {
            var wanted = value is IntroStep or LessonStep || (value is >= ClipsStep and <= PublishStep && HasEpisode) ? value : LessonStep;
            if (wanted == _step)
            {
                if (wanted != value) OnPropertyChanged(nameof(Step));
                return;
            }
            _step = wanted;
            OnPropertyChanged(nameof(Step));
            OnPropertyChanged(nameof(ShowIntro));
            OnPropertyChanged(nameof(ShowTopic));
            OnPropertyChanged(nameof(ShowDeck));
            OnPropertyChanged(nameof(StepHint));
            if (wanted == LessonStep) _ = Guard(RefreshLibraryAsync);
            ErrorMessage = null; // an error belongs to the page it happened on
        }
    }

    // While a script is being written the deck page shows in place of the others, with placeholder cards.
    public bool ShowIntro => Step == IntroStep && !IsCreating;

    public bool ShowTopic => Step == LessonStep && !IsCreating;

    public bool ShowDeck => Step == ClipsStep || IsCreating;

    public string StepHint => StepHints[Math.Clamp(Step, 0, StepHints.Length - 1)];

    /// <summary>Which half of the inspector is showing: 0 the clip's words, 1 its picture.</summary>
    [ObservableProperty]
    public partial int InspectorTab { get; set; }

    /// <summary>For the keyboard shortcuts.</summary>
    public void GoTo(int step) => Step = step;

    /// <summary>
    /// Where the app opens with no episode to go back to: the intro until the channel has a name, as nothing is
    /// branded without one, and the lesson after that.
    /// </summary>
    private int FirstStep => _kit.ChannelName.Length > 0 ? LessonStep : IntroStep;

    // ---- The bar under the header ----

    /// <summary>What the step asks for, in order, each ticked off as it is done.</summary>
    public IReadOnlyList<GuideItem> Guide
    {
        get
        {
            var items = Step switch
            {
                IntroStep => IntroGuide(),
                LessonStep => LessonGuide(),
                ClipsStep => ClipsGuide(),
                PreviewStep => PreviewGuide(),
                _ => PublishGuide(),
            };
            // The first item still needed is the one to do next. An optional one never is: the step is done without it.
            var next = items.FindIndex(i => !i.Done && !i.Optional);
            return [.. items.Select((item, i) => i == next ? item with { Next = true } : item)];
        }
    }

    private bool StepDone => Guide.All(i => i.Done || i.Optional);

    private List<GuideItem> IntroGuide() =>
    [
        new("Channel name", _kit.ChannelName.Length > 0),
        new("Logo or mascot", _kit.Logo is not null || _kit.Mascot is not null, Optional: true),
        new("Watch the intro", IntroPreviewPath is not null, Optional: true),
    ];

    // A lesson typed in while an episode is open is the start of the next one, so its script is not written yet.
    private List<GuideItem> LessonGuide()
    {
        var typed = !string.IsNullOrWhiteSpace(TopicInput);
        var written = HasEpisode && !typed;
        return
        [
            new("What you taught", typed || written),
            new(Footage.Count == 0 ? "Films" : $"Films · {Footage.Count}", Footage.Count > 0, Optional: true),
            new("Write the script", written),
        ];
    }

    private List<GuideItem> ClipsGuide()
    {
        var voiced = Clips.Count(c => c.Measured is not null || c.OwnSound);
        var items = new List<GuideItem> { new($"Voices · {voiced} of {Clips.Count}", voiced == Clips.Count) };
        if (Episode is { } episode && PicturesAvailable)
        {
            var wanted = CostEstimate.For(episode, _imageModel, Quality).ClipIds.Count;
            items.Add(new(wanted == 0 ? "Pictures · all made" : $"Pictures · {wanted} to make", wanted == 0));
        }
        var off = Durations.RuntimeWarning(Runtime);
        items.Add(new(off is null ? $"Running time {Runtime:m\\:ss}" : $"Running time {Runtime:m\\:ss}, {off}", off is null, Optional: true));
        return items;
    }

    private List<GuideItem> PreviewGuide() =>
    [
        new("Build the preview", HasPreview),
        new("Music", HasMusic, Optional: true),
    ];

    private List<GuideItem> PublishGuide()
    {
        var export = EpisodeFolder is { } folder ? Path.Combine(folder, "export") : null;
        bool Any(string? dir, Func<string, bool> keep) => dir is not null && Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.mp4").Any(keep);
        var teasers = Any(export is null ? null : Path.Combine(export, "teasers"), _ => true);
        var master = Any(export, f => !Path.GetFileName(f).Equals("preview.mp4", StringComparison.OrdinalIgnoreCase));
        return
        [
            new("Teasers", teasers, Optional: true),
            new("Render the master video", master),
        ];
    }

    /// <summary>
    /// The bar's one button: the next thing the step needs that has no button of its own on the page (the voices and
    /// pictures, on the clips page), or moving on to the next step once this one is done. Empty when there is neither.
    /// </summary>
    public string GuideActionLabel => GuideAction().Label;

    public bool HasGuideAction => GuideAction().Command is not null;

    /// <summary>True when the bar's button is the thing to do next, and not the page's own: it is then the one lit.</summary>
    public bool GuideActionIsNext => GuideAction().IsNext;

    private (string Label, System.Windows.Input.ICommand? Command, bool IsNext) GuideAction()
    {
        if (Step == ClipsStep && !StepDone && NextJob() is var (label, command) && command != MakeDraftCommand)
            return (label, command, true);
        if (Step < PublishStep && (Step == IntroStep || HasEpisode))
            return ($"Continue to {StepNames[Step + 1]}", ContinueCommand, StepDone && !PageButtonIsNext);
        return ("", null, false);
    }

    // The page's own big button is still the thing to do: writing a script typed in, or building the preview.
    private bool PageButtonIsNext => Step switch
    {
        IntroStep => _kit.ChannelName.Length > 0 && IntroPreviewPath is null,
        LessonStep => !string.IsNullOrWhiteSpace(TopicInput),
        PreviewStep => !HasPreview,
        _ => false,
    };

    /// <summary>The lesson page's Write the script button is lit while a lesson is typed in: it is what to do next.</summary>
    public bool WriteIsNext => !string.IsNullOrWhiteSpace(TopicInput);

    /// <summary>The intro page's Show the intro button is lit until the intro has been watched this time.</summary>
    public bool ShowIntroIsNext => IntroPreviewPath is null;

    [RelayCommand]
    private void Continue() => Step = Math.Min(Step + 1, PublishStep);

    [RelayCommand]
    private void RunGuideAction()
    {
        if (GuideAction().Command is { } command && command.CanExecute(null)) command.Execute(null);
    }

    // What the bar reads from. Any of these changing may tick an item off, so the bar is told to look again.
    private static readonly HashSet<string> GuideSources =
    [
        nameof(Step), nameof(TopicInput), nameof(Episode), nameof(Preview), nameof(LastExport), nameof(HasTeasers), nameof(MakeNextLabel),
        nameof(IntroPreviewPath), nameof(SeriesChannel), nameof(SeriesBrand), nameof(IsConnected), nameof(Runtime),
    ];

    private void RefreshGuide(PropertyChangedEventArgs e)
    {
        if (GuideSources.Contains(e.PropertyName ?? "")) RefreshGuide();
    }

    private void RefreshGuide()
    {
        foreach (var name in (string[])[nameof(Guide), nameof(GuideActionLabel), nameof(HasGuideAction), nameof(GuideActionIsNext), nameof(WriteIsNext), nameof(ShowIntroIsNext)])
            OnPropertyChanged(name);
    }
}
