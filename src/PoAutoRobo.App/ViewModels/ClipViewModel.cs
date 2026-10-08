using CommunityToolkit.Mvvm.ComponentModel;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;

namespace PoAutoRobo.App.ViewModels;

/// <summary>One card in the clip deck. Edits go through <paramref name="edit"/> so the episode stays the single source of truth.</summary>
public partial class ClipViewModel(Clip clip, Action<Func<Episode, Episode>> edit) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(DurationText), nameof(KindText), nameof(TierIndex), nameof(HostVisible), nameof(Dialogue), nameof(Pose), nameof(IsStale), nameof(HasUserVideo), nameof(DurationWarning), nameof(HasDurationWarning), nameof(CanPickKind), nameof(KindIndex), nameof(ThumbnailPath))]
    public partial Clip Clip { get; set; } = clip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    public partial int Number { get; set; }

    public Guid Id => Clip.Id;

    public string Heading => $"{Number} · {Clip.Title}";

    public string DurationText => $"{Durations.Estimate(Clip.Active.Dialogue).TotalSeconds:0} s";

    /// <summary>Set when the dialogue runs outside 15 to 60 seconds. A flag only: the clip still exports.</summary>
    public string? DurationWarning => Durations.Warning(Clip);

    public bool HasDurationWarning => DurationWarning is not null;

    public string Dialogue => Clip.Active.Dialogue;

    public string Pose => Clip.Active.Pose;

    public bool IsStale => Clip.Visual.Stale;

    /// <summary>The clip's generated picture, when it has one that is still on disk.</summary>
    public string? ThumbnailPath =>
        Clip.Visual.MediaPaths?.FirstOrDefault() is { } path && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;

    public bool HasUserVideo => Clip.Visual.Kind == VisualKind.UserVideo;

    public string KindText => Clip.Visual.Kind switch
    {
        VisualKind.Still => "Still panel",
        VisualKind.MultiPanel => "Panel sequence",
        VisualKind.AiVideo => "AI video",
        VisualKind.UserVideo => "My video",
        _ => "Title card",
    };

    private static readonly VisualKind[] PickableKinds = [VisualKind.Still, VisualKind.MultiPanel, VisualKind.AiVideo, VisualKind.TitleCard];

    public bool CanPickKind => !HasUserVideo;

    /// <summary>Picture type as the inspector's list shows it; -1 for the user's own video, which is not in the list.</summary>
    public int KindIndex
    {
        get => Array.IndexOf(PickableKinds, Clip.Visual.Kind);
        set
        {
            if (value >= 0 && value < PickableKinds.Length && value != KindIndex)
                edit(e => EpisodeEditor.SetKind(e, Id, PickableKinds[value]));
        }
    }

    public int TierIndex
    {
        get => (int)Clip.ActiveTier;
        set
        {
            // The control reports -1 while it rebuilds; ignore that and no-op repeats.
            if (value is >= 0 and <= 2 && value != TierIndex)
                edit(e => EpisodeEditor.SetTier(e, Id, (Tier)value));
        }
    }

    public bool HostVisible
    {
        get => Clip.HostVisible;
        set
        {
            if (value != HostVisible)
                edit(e => EpisodeEditor.SetHostVisible(e, Id, value));
        }
    }
}
