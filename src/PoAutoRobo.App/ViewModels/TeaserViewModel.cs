using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PoAutoRobo.App.ViewModels;

/// <summary>One suggested teaser as its card shows it: the hook to edit, the moments to move, and whether to keep it.</summary>
public sealed partial class TeaserViewModel : ObservableObject
{
    private readonly IReadOnlyList<Clip> _clips;

    public TeaserViewModel(Teaser teaser, IReadOnlyList<Clip> clips)
    {
        _clips = clips;
        Hook = teaser.Hook;
        var choices = clips.Select((c, i) => $"{i + 1} · {c.Title}").ToList();
        foreach (var moment in teaser.Moments)
        {
            var row = new MomentViewModel(choices, Math.Max(0, clips.ToList().FindIndex(c => c.Id == moment.ClipId)), moment.Start.TotalSeconds, moment.Length.TotalSeconds);
            row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Poster));
            Moments.Add(row);
        }
    }

    /// <summary>The line shown across the top of the teaser.</summary>
    [ObservableProperty]
    public partial string Hook { get; set; }

    /// <summary>Rendered with the others; off leaves it out.</summary>
    [ObservableProperty]
    public partial bool Keep { get; set; } = true;

    public ObservableCollection<MomentViewModel> Moments { get; } = [];

    /// <summary>The picture of the clip the teaser opens on, for the card; empty when that clip has none yet.</summary>
    public string Poster => Moments.Count > 0 && Moments[0].ClipIndex < _clips.Count ? _clips[Moments[0].ClipIndex].Picture() ?? "" : "";

    /// <summary>The teaser as now set. The rules are applied again when it is rendered, so a moved moment still fits.</summary>
    public Teaser ToTeaser() => new(Hook, [.. Moments.Where(m => m.ClipIndex >= 0 && m.ClipIndex < _clips.Count)
        .Select(m => new TeaserMoment(_clips[m.ClipIndex].Id, TimeSpan.FromSeconds(double.IsNaN(m.Start) ? 0 : Math.Max(0, m.Start)), TimeSpan.FromSeconds(m.Length)))]);
}

/// <summary>One moment of a teaser card: which clip it is cut from, and where in that clip it starts.</summary>
public sealed partial class MomentViewModel(IReadOnlyList<string> clipChoices, int clipIndex, double start, double length) : ObservableObject
{
    /// <summary>"N · title" for every clip, to swap the moment to another.</summary>
    public IReadOnlyList<string> ClipChoices { get; } = clipChoices;

    [ObservableProperty]
    public partial int ClipIndex { get; set; } = clipIndex;

    /// <summary>Seconds from the start of its clip; a number box gives a double.</summary>
    [ObservableProperty]
    public partial double Start { get; set; } = start;

    public double Length { get; } = length;

    public string LengthText => $"{Length:0.0} s";
}
