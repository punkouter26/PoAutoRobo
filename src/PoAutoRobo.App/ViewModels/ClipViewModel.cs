using CommunityToolkit.Mvvm.ComponentModel;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;

namespace PoAutoRobo.App.ViewModels;

/// <summary>One card in the clip deck. Edits go through <paramref name="edit"/> so the episode stays the single source of truth.</summary>
public partial class ClipViewModel(Clip clip, Action<Func<Episode, Episode>> edit) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(DurationText), nameof(KindText), nameof(TierIndex), nameof(HostVisible), nameof(Dialogue), nameof(Pose), nameof(IsStale), nameof(HasUserVideo))]
    public partial Clip Clip { get; set; } = clip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    public partial int Number { get; set; }

    public Guid Id => Clip.Id;

    public string Heading => $"{Number} · {Clip.Title}";

    public string DurationText => $"{Durations.Estimate(Clip.Active.Dialogue).TotalSeconds:0} s";

    public string Dialogue => Clip.Active.Dialogue;

    public string Pose => Clip.Active.Pose;

    public bool IsStale => Clip.Visual.Stale;

    public bool HasUserVideo => Clip.Visual.Kind == VisualKind.UserVideo;

    public string KindText => Clip.Visual.Kind switch
    {
        VisualKind.Still => "Still panel",
        VisualKind.MultiPanel => "Panel sequence",
        VisualKind.AiVideo => "AI video",
        VisualKind.UserVideo => "My video",
        _ => "Title card",
    };

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
