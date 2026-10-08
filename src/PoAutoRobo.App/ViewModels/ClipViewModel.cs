using CommunityToolkit.Mvvm.ComponentModel;

namespace PoAutoRobo.App.ViewModels;

/// <summary>One entry in a drop-down list: the words shown and the value they stand for, kept together so the two cannot drift apart.</summary>
public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label; // what the list shows
}

/// <summary>One card in the clip deck. Edits go through <paramref name="edit"/> so the episode stays the single source of truth.</summary>
/// <param name="pickTier">Switches depth; the owner writes the depth first when the clip does not have it yet.</param>
public partial class ClipViewModel(Clip clip, Action<Func<Episode, Episode>> edit, Action<ClipViewModel, Tier> pickTier) : ObservableObject
{
    // AI video is not offered: until a video service is wired in, such a clip is drawn as one still.
    public static Choice<VisualKind>[] KindChoices { get; } =
    [
        new("Still panel", VisualKind.Still), new("Panel sequence", VisualKind.MultiPanel), new("Title card", VisualKind.TitleCard),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Heading), nameof(Summary), nameof(Warnings), nameof(TierIndex), nameof(HostVisible), nameof(Dialogue), nameof(Pose), nameof(VisualPrompt),
        nameof(IsStale), nameof(HasUserVideo), nameof(CanPickKind), nameof(KindIndex), nameof(ThumbnailPath))]
    public partial Clip Clip { get; set; } = clip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    public partial int Number { get; set; }

    /// <summary>How long the recorded narration really runs; null until this wording has been recorded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial TimeSpan? Measured { get; set; }

    /// <summary>Figures and code names in the dialogue that are not in the episode's sources; empty when there are none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Warnings))]
    public partial string Unverified { get; set; } = "";

    /// <summary>True while something is being made for this clip: a picture, a recording or a new depth.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Working))]
    public partial bool IsBusy { get; set; }

    /// <summary>How far this clip's picture has got in the render under way, 0 to 100; 0 when it is not being drawn.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Working), nameof(ProgressUnknown))]
    public partial double Progress { get; set; }

    /// <summary>Something is being made for this clip right now; the card shows a ring.</summary>
    public bool Working => IsBusy || Progress is > 0 and < 100;

    /// <summary>The ring spins without a figure until the render says how far it has got.</summary>
    public bool ProgressUnknown => Progress <= 0;

    public Guid Id => Clip.Id;

    public string Title => Clip.Title;

    public string Heading => $"{Number} · {Clip.Title}";

    /// <summary>The recorded length when there is one, otherwise an estimate from the word count.</summary>
    public TimeSpan Duration => Measured ?? Durations.Estimate(Clip.Active.Dialogue);

    /// <summary>The clip's state in one line for its card: depth, picture type and length.</summary>
    public string Summary
    {
        get
        {
            var kind = KindChoices.FirstOrDefault(k => k.Value == Clip.Visual.Kind)?.Label ?? (HasUserVideo ? "My video" : "Still panel");
            var length = Measured is { } real ? $"{real.TotalSeconds:0} s" : $"about {Duration.TotalSeconds:0} s";
            return $"{Clip.ActiveTier} · {kind} · {length}{(Clip.HostVisible ? "" : " · voice only")}";
        }
    }

    /// <summary>Everything about this clip worth a second look, one to a line; empty when there is nothing.</summary>
    public string Warnings =>
        string.Join('\n', new[] { IsStale ? "The picture is out of date." : "", Durations.Warning(Clip) ?? "", Unverified }.Where(line => line.Length > 0));

    public string Dialogue => Clip.Active.Dialogue;

    public string Pose => Clip.Active.Pose;

    public string VisualPrompt => Clip.Active.VisualPrompt;

    public bool IsStale => Clip.Visual.Stale;

    /// <summary>The clip's generated picture, when it has one that is still on disk; empty when it has none.</summary>
    public string ThumbnailPath => Clip.Picture() ?? "";

    public bool HasUserVideo => Clip.Visual.Kind == VisualKind.UserVideo;

    public bool CanPickKind => !HasUserVideo;

    /// <summary>Picture type as the inspector's list shows it; -1 for a kind that is not in the list.</summary>
    public int KindIndex
    {
        get => Array.FindIndex(KindChoices, k => k.Value == Clip.Visual.Kind);
        set
        {
            if (value >= 0 && value < KindChoices.Length && value != KindIndex)
                edit(e => EpisodeEditor.SetKind(e, Id, KindChoices[value].Value));
        }
    }

    public int TierIndex
    {
        get => (int)Clip.ActiveTier;
        set
        {
            // The control reports -1 while it rebuilds; ignore that and no-op repeats.
            if (value is >= 0 and <= 2 && value != TierIndex)
                pickTier(this, (Tier)value);
        }
    }

    /// <summary>A picture for this clip has appeared on disk without the clip itself changing.</summary>
    public void PictureChanged() => OnPropertyChanged(nameof(ThumbnailPath));

    /// <summary>Puts the depth selector back on the depth the clip is really on, after a switch that did not happen.</summary>
    public void ResetTierSelector() => OnPropertyChanged(nameof(TierIndex));

    public bool HostVisible
    {
        get => Clip.HostVisible;
        set
        {
            if (value != HostVisible)
                edit(e => EpisodeEditor.SetHostVisible(e, Id, value));
        }
    }

    public override string ToString() => Heading; // what a screen reader calls the card
}
