using CommunityToolkit.Mvvm.ComponentModel;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;

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
    public static Choice<VisualKind>[] KindChoices { get; } =
    [
        new("Still panel", VisualKind.Still), new("Panel sequence", VisualKind.MultiPanel),
        new("AI video", VisualKind.AiVideo), new("Title card", VisualKind.TitleCard),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(DurationText), nameof(KindText), nameof(TierIndex), nameof(TierText), nameof(HostVisible), nameof(IsOffScreen), nameof(Dialogue), nameof(Pose), nameof(IsStale), nameof(HasUserVideo), nameof(DurationWarning), nameof(HasDurationWarning), nameof(CanPickKind), nameof(KindIndex), nameof(ThumbnailPath))]
    public partial Clip Clip { get; set; } = clip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    public partial int Number { get; set; }

    /// <summary>How long the recorded narration really runs; null until this wording has been recorded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    public partial TimeSpan? Measured { get; set; }

    /// <summary>Figures and code names in the dialogue that are not in the episode's sources; null when there are none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnverified))]
    public partial string? Unverified { get; set; }

    /// <summary>True while something is being made for this clip: a picture, a recording or a new depth.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public Guid Id => Clip.Id;

    public string Heading => $"{Number} · {Clip.Title}";

    /// <summary>The recorded length when there is one, otherwise an estimate from the word count.</summary>
    public TimeSpan Duration => Measured ?? Durations.Estimate(Clip.Active.Dialogue);

    public string DurationText => Measured is { } real ? $"{real.TotalSeconds:0} s" : $"about {Duration.TotalSeconds:0} s";

    /// <summary>Set when the dialogue runs outside 15 to 60 seconds. A flag only: the clip still exports.</summary>
    public string? DurationWarning => Durations.Warning(Clip);

    public bool HasDurationWarning => DurationWarning is not null;

    public bool HasUnverified => Unverified is not null;

    public string Dialogue => Clip.Active.Dialogue;

    public string Pose => Clip.Active.Pose;

    public bool IsStale => Clip.Visual.Stale;

    /// <summary>The clip's generated picture, when it has one that is still on disk.</summary>
    public string? ThumbnailPath =>
        Clip.Visual.MediaPaths?.FirstOrDefault() is { } path && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;

    public bool HasUserVideo => Clip.Visual.Kind == VisualKind.UserVideo;

    public string KindText => KindChoices.FirstOrDefault(k => k.Value == Clip.Visual.Kind)?.Label ?? "My video";

    public bool CanPickKind => !HasUserVideo;

    /// <summary>Picture type as the inspector's list shows it; -1 for the user's own video, which is not in the list.</summary>
    public int KindIndex
    {
        get => Array.FindIndex(KindChoices, k => k.Value == Clip.Visual.Kind);
        set
        {
            if (value >= 0 && value < KindChoices.Length && value != KindIndex)
                edit(e => EpisodeEditor.SetKind(e, Id, KindChoices[value].Value));
        }
    }

    public string TierText => Clip.ActiveTier.ToString();

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

    public bool IsOffScreen => !Clip.HostVisible;

    public override string ToString() => Heading; // what a screen reader calls the card
}
