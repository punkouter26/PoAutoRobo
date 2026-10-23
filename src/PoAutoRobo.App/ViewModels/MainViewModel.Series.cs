using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PoAutoRobo.App.ViewModels;

/// <summary>A ready-made look for the intro: its name, its accent colour as #RRGGBB, and its layout.</summary>
public sealed record IntroStart(string Name, string Accent, IntroLayout Layout)
{
    /// <summary>The colour, for the small sketch of the look on its tile.</summary>
    public Windows.UI.Color Colour => Windows.UI.Color.FromArgb(255, Convert.ToByte(Accent[1..3], 16), Convert.ToByte(Accent[3..5], 16), Convert.ToByte(Accent[5..7], 16));
}

/// <summary>The series brand kit: what every episode shares, set up once on the Intro step.</summary>
public partial class MainViewModel
{
    // Each look with the words shown for it, kept together so the list cannot drift out of step with the looks there are.
    public static Choice<Look>[] LookChoices { get; } =
    [
        new("Comic book", Look.Comic), new("Photograph", Look.Photoreal), new("Flat vector", Look.FlatVector), new("Cinematic", Look.Cinematic),
    ];

    private SeriesKit _kit = SeriesKit.Load(AppPaths.Series);

    /// <summary>The kit and where its files are, for the intro, the outro and new episodes.</summary>
    public Brand SeriesBrand => new(_kit, AppPaths.Series);

    // The intro page's fields. Each change is saved at once: there is nothing to lose by leaving.

    public string SeriesChannel
    {
        get => _kit.ChannelName;
        set => Store(_kit with { ChannelName = value.Trim() });
    }

    public string SeriesTagline
    {
        get => _kit.Tagline;
        set => Store(_kit with { Tagline = value.Trim() });
    }

    /// <summary>How long the outro runs, in seconds; a number box gives a double.</summary>
    public double SeriesOutro
    {
        get => _kit.OutroSeconds;
        set => Store(_kit with { OutroSeconds = Math.Clamp((int)Math.Round(double.IsNaN(value) ? 15 : value), SeriesKit.MinOutroSeconds, SeriesKit.MaxOutroSeconds) });
    }

    /// <summary>The outro runs other than the standard fifteen seconds, so its length stays in view without advanced controls.</summary>
    public bool OutroTuned => _kit.OutroSeconds != new SeriesKit().OutroSeconds;

    /// <summary>How every episode is told, as an index into <see cref="StructureChoices"/>.</summary>
    public int SeriesStructureIndex
    {
        get => Math.Max(0, Array.FindIndex(StructureChoices, c => c.Value == _kit.Structure));
        set
        {
            if (value >= 0 && value < StructureChoices.Length) Store(_kit with { Structure = StructureChoices[value].Value });
        }
    }

    /// <summary>
    /// The open episode looks other than the series now does: made before the picture style, captions or outro music
    /// changed. The Intro step then offers to bring it into line.
    /// </summary>
    public bool EpisodeOffSeriesLook => Episode is { } episode && (EpisodeEditor.UseSeriesLook(episode, _kit, null) != episode
        || (_kit.Music is { } music && !string.Equals(Path.GetFileName(episode.MusicPath), "music-" + music, StringComparison.OrdinalIgnoreCase)));

    public int SeriesLookIndex
    {
        get => Math.Max(0, Array.FindIndex(LookChoices, c => c.Value == _kit.Look));
        set
        {
            if (value >= 0 && value < LookChoices.Length) Store(_kit with { Look = LookChoices[value].Value });
        }
    }

    /// <summary>New episodes start with the caption style last picked on the Preview step, so it is chosen in one place.</summary>
    private void RememberCaptionPreset(CaptionPreset preset) => Store(_kit with { Captions = _kit.Captions with { Preset = preset } });

    /// <summary>The accent colour as #RRGGBB. Anything else is kept in the box but not saved.</summary>
    public string SeriesAccent
    {
        get => _kit.Captions.AccentColor;
        set
        {
            if (AssCaptions.IsHexColour(value.Trim())) Store(_kit with { Captions = _kit.Captions with { AccentColor = value.Trim().ToUpperInvariant() } });
        }
    }

    // With nothing chosen, the app makes its own of each, so none of them is ever simply missing.
    public string SeriesLogoLabel => _kit.Logo ?? "Made for you";

    public string SeriesStingLabel => _kit.Sting ?? "Made for you";

    public string SeriesMusicLabel => _kit.Music ?? "Made for you";

    public string SeriesMascotLabel => _kit.Mascot ?? "Made for you";

    /// <summary>The intro last drawn for the Intro step; null until one is asked for.</summary>
    [ObservableProperty]
    public partial string? IntroPreviewPath { get; set; }

    /// <summary>The outro last drawn for the Intro step; null until one is asked for.</summary>
    [ObservableProperty]
    public partial string? OutroPreviewPath { get; set; }

    /// <summary>
    /// A still of the intro (or of the outro, once Show the outro has been pressed) as the settings stand now, redrawn a
    /// moment after each change so the Intro step follows what is done on the left. Null until the first is drawn.
    /// </summary>
    [ObservableProperty]
    public partial string? StillPath { get; set; }

    private bool _stillOfOutro;
    private CancellationTokenSource? _still;

    // A burst of changes, such as typing a name, draws once, a moment after the last. Each still is drawn here, for free.
    private void RedrawStill()
    {
        if (!FfmpegAvailable || Step != IntroStep) return;
        _still?.Cancel();
        _still = new CancellationTokenSource();
        _ = DrawStillAsync(_still.Token);
    }

    private async Task DrawStillAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
            var number = Episode?.Number is > 0 and var n ? n : Math.Max(1, (int)EpisodeNumber);
            var still = await _builder.BookendStillAsync(!_stillOfOutro, SeriesBrand, number, ct);
            if (!ct.IsCancellationRequested) StillPath = still;
        }
        catch (OperationCanceledException)
        {
            // A newer change has asked for its own still.
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // The still is a help, not a job: the intro is still shown in full by its button, which says what went wrong.
        }
    }

    public static Choice<IntroLayout>[] LayoutChoices { get; } =
    [
        new("Side", IntroLayout.Side), new("Centre", IntroLayout.Centre), new("Lower band", IntroLayout.Lower),
    ];

    public int SeriesLayoutIndex
    {
        get => Math.Max(0, Array.FindIndex(LayoutChoices, c => c.Value == _kit.Layout));
        set
        {
            if (value >= 0 && value < LayoutChoices.Length) Store(_kit with { Layout = LayoutChoices[value].Value });
        }
    }

    /// <summary>Ready-made looks for the intro to start from. Each sets the colour and the layout, and both can be changed after.</summary>
    public static IntroStart[] IntroStarts { get; } =
    [
        new("Workshop", "#22CC88", IntroLayout.Side), new("Blueprint", "#2F80ED", IntroLayout.Centre),
        new("Safety amber", "#FFB020", IntroLayout.Lower), new("Night shift", "#8B5CF6", IntroLayout.Centre),
    ];

    /// <summary>Gives the intro one of the ready-made looks: its colour, which the captions and straps share, and its layout.</summary>
    public void UseIntroStart(IntroStart start) =>
        Store(_kit with { Layout = start.Layout, Captions = _kit.Captions with { AccentColor = start.Accent } });

    private void Store(SeriesKit kit)
    {
        if (kit == _kit) return;
        _kit = kit;
        try
        {
            kit.Save(AppPaths.Series);
        }
        catch (IOException e)
        {
            ErrorMessage = $"The series settings could not be saved: {e.Message}";
        }
        foreach (var name in SeriesFields)
            OnPropertyChanged(name); // the page's fields and labels all read from the kit
        RedrawStill();
    }

    private static readonly string[] SeriesFields =
    [
        nameof(SeriesChannel), nameof(SeriesTagline), nameof(SeriesOutro), nameof(SeriesLookIndex), nameof(SeriesAccent), nameof(SeriesLayoutIndex),
        nameof(SeriesLogoLabel), nameof(SeriesStingLabel), nameof(SeriesMusicLabel), nameof(SeriesMascotLabel), nameof(SeriesBrand),
        nameof(OutroTuned), nameof(SeriesStructureIndex), nameof(StructureBeats), nameof(EpisodeOffSeriesLook),
    ];

    /// <summary>
    /// Takes a file the user picked into the kit as its logo, sting, music or mascot. The kit keeps its own copy, so the
    /// picked file can be moved or deleted afterwards.
    /// </summary>
    public void SetSeriesFile(string role, string path) => _ = Guard(async () =>
    {
        var name = await Task.Run(() => SeriesKit.Import(AppPaths.Series, path, role));
        Store(role switch
        {
            "logo" => _kit with { Logo = name },
            "sting" => _kit with { Sting = name },
            "music" => _kit with { Music = name },
            _ => _kit with { Mascot = name },
        });
        RedrawStill(); // a new picture under the same name changes nothing in the kit, but changes what is drawn
    });

    public void ClearSeriesFile(string role) => Store(role switch
    {
        "logo" => _kit with { Logo = null },
        "sting" => _kit with { Sting = null },
        "music" => _kit with { Music = null },
        _ => _kit with { Mascot = null },
    });

    /// <summary>True when a host design has been chosen, which can then stand as the series mascot.</summary>
    [ObservableProperty]
    public partial bool HasHostDesign { get; private set; } = File.Exists(AppPaths.HostSheet);

    // ---- A logo, made the way the host is: three candidates, one chosen ----

    /// <summary>Logos drawn for the channel to choose from; empty until asked for, and again once one is chosen.</summary>
    public ObservableCollection<string> LogoCandidates { get; } = [];

    /// <summary>What making the logo candidates costs, said beside the button before anything is made.</summary>
    public string LogoCandidatesCost => CostEstimate.PriceOf(_imageModel, Quality) is { } each
        ? $"Three designs, drawn from your host when you have one (about {SpendLog.Money(3 * each)})."
        : "Three designs, drawn from your host when you have one. The cost depends on your Azure pricing.";

    /// <summary>Draws three logo designs for the channel, after saying what they cost and checking the month's budget.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task MakeLogoCandidatesAsync(CancellationToken ct)
    {
        ErrorMessage = null;
        if (CurrentVisuals is not { } visuals) return;
        if (_kit.ChannelName.Length == 0)
        {
            ErrorMessage = "Name the channel first: the logos are drawn for it.";
            return;
        }
        var dollars = 3 * (CostEstimate.PriceOf(_imageModel, Quality) ?? 0);
        if (OverBudget(dollars) is { } refusal)
        {
            ErrorMessage = refusal;
            return;
        }
        var from = HasHostDesign ? "your host, simplified to an emblem" : "a friendly robot head";
        if (!await ConfirmSpend("Make 3 logos?", $"Draws three logo designs for {_kit.ChannelName} from {from}, in the series colour, on nothing so they sit on any background. You pick one, or none. Asking again with nothing changed shows the same three, free.", "Make them", dollars))
            return;
        await Guard(async () =>
        {
            var logos = await visuals.CandidateLogosAsync(_kit.ChannelName, _kit.Captions.AccentColor, 3, ct);
            LogoCandidates.Clear();
            foreach (var logo in logos)
                LogoCandidates.Add(logo);
            StatusMessage = "Three logos drawn. Click the one to use.";
        });
    }

    /// <summary>Makes a drawn candidate the series logo. The kit keeps its own copy, as it does of a logo picked from a file.</summary>
    public void UseLogo(string path)
    {
        SetSeriesFile("logo", path);
        LogoCandidates.Clear();
    }

    [RelayCommand]
    private void UseHostAsMascot()
    {
        if (HasHostDesign) SetSeriesFile("mascot", AppPaths.HostSheet);
    }

    /// <summary>Makes a short draft of the intro as it will look, with its sound. Built here: it costs nothing.</summary>
    [RelayCommand]
    private Task PreviewIntroAsync(CancellationToken ct) => Guard(async () =>
    {
        if (!FfmpegAvailable)
            throw new InvalidOperationException("FFmpeg is needed to draw the intro. Install it, then restart the app.");
        var number = Episode?.Number is > 0 and var n ? n : Math.Max(1, (int)EpisodeNumber);
        _stillOfOutro = false;
        IntroPreviewPath = await _builder.PreviewIntroAsync(SeriesBrand, number, ct);
    });

    /// <summary>Makes a draft of the outro as it will end every episode, at its full length. Built here: it costs nothing.</summary>
    [RelayCommand]
    private Task PreviewOutroAsync(CancellationToken ct) => Guard(async () =>
    {
        if (!FfmpegAvailable)
            throw new InvalidOperationException("FFmpeg is needed to draw the outro. Install it, then restart the app.");
        _stillOfOutro = true;
        OutroPreviewPath = await _builder.PreviewOutroAsync(SeriesBrand, ct);
    });

    /// <summary>
    /// Gives the open episode the series look again (picture style, captions, music), as one step Undo reverses: the
    /// way an episode made before the look changed catches up. An episode has no look of its own to set otherwise.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasEpisode))]
    private Task UseSeriesLookAsync() => Guard(async () =>
    {
        if (Episode is null || EpisodeFolder is not { } folder) return;
        var music = SeriesKit.PathOf(AppPaths.Series, _kit.Music) is { } track ? await ImportAsync(track, folder, "music-" + Path.GetFileName(track), CancellationToken.None) : null;
        Edit(e => EpisodeEditor.UseSeriesLook(e, _kit, music));
        StatusMessage = "The episode now has the series look.";
    });
}
