using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PoAutoRobo.App.ViewModels;

/// <summary>The series brand kit: what every episode shares, set up once in the Series dialog.</summary>
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

    // The dialog's fields. Each change is saved at once: there is nothing to lose by closing the dialog.

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

    public int SeriesLookIndex
    {
        get => Math.Max(0, Array.FindIndex(LookChoices, c => c.Value == _kit.Look));
        set
        {
            if (value >= 0 && value < LookChoices.Length) Store(_kit with { Look = LookChoices[value].Value });
        }
    }

    public int SeriesCaptionIndex
    {
        get => Math.Max(0, Array.FindIndex(CaptionChoices, c => c.Value == _kit.Captions.Preset));
        set
        {
            if (value >= 0 && value < CaptionChoices.Length) Store(_kit with { Captions = _kit.Captions with { Preset = CaptionChoices[value].Value } });
        }
    }

    /// <summary>The accent colour as #RRGGBB. Anything else is kept in the box but not saved.</summary>
    public string SeriesAccent
    {
        get => _kit.Captions.AccentColor;
        set
        {
            if (AssCaptions.IsHexColour(value.Trim())) Store(_kit with { Captions = _kit.Captions with { AccentColor = value.Trim().ToUpperInvariant() } });
        }
    }

    public string SeriesLogoLabel => _kit.Logo ?? "None";

    public string SeriesStingLabel => _kit.Sting ?? "None";

    public string SeriesMusicLabel => _kit.Music ?? "None";

    public string SeriesMascotLabel => _kit.Mascot ?? "None";

    /// <summary>The draft intro last made for the dialog's preview; null until one is asked for.</summary>
    [ObservableProperty]
    public partial string? IntroPreviewPath { get; set; }

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
            OnPropertyChanged(name); // the dialog's fields and labels all read from the kit
    }

    private static readonly string[] SeriesFields =
    [
        nameof(SeriesChannel), nameof(SeriesTagline), nameof(SeriesOutro), nameof(SeriesLookIndex), nameof(SeriesCaptionIndex), nameof(SeriesAccent),
        nameof(SeriesLogoLabel), nameof(SeriesStingLabel), nameof(SeriesMusicLabel), nameof(SeriesMascotLabel), nameof(SeriesBrand),
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
    });

    public void ClearSeriesFile(string role) => Store(role switch
    {
        "logo" => _kit with { Logo = null },
        "sting" => _kit with { Sting = null },
        "music" => _kit with { Music = null },
        _ => _kit with { Mascot = null },
    });

    /// <summary>True when a host design has been chosen, which can then stand as the series mascot.</summary>
    public static bool HasHostDesign => File.Exists(AppPaths.HostSheet);

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
        IntroPreviewPath = await _builder.PreviewIntroAsync(SeriesBrand, number, ct);
    });

    /// <summary>Gives the open episode the series look again (picture style, captions, music), as one step Undo reverses.</summary>
    [RelayCommand(CanExecute = nameof(HasEpisode))]
    private Task UseSeriesLookAsync() => Guard(async () =>
    {
        if (Episode is null || EpisodeFolder is not { } folder) return;
        var music = SeriesKit.PathOf(AppPaths.Series, _kit.Music) is { } track ? await ImportAsync(track, folder, "music-" + Path.GetFileName(track), CancellationToken.None) : null;
        Edit(e => EpisodeEditor.UseSeriesLook(e, _kit, music));
        StatusMessage = "The episode now has the series look.";
    });
}
