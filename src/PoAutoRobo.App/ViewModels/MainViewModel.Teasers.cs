using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace PoAutoRobo.App.ViewModels;

/// <summary>Teasers: ten-second upright cuts of the episode for other video sites, suggested, adjusted, then rendered.</summary>
public partial class MainViewModel
{
    /// <summary>The suggested teasers, as cards on the Publish step.</summary>
    public ObservableCollection<TeaserViewModel> TeaserCards { get; } = [];

    public bool HasTeasers => TeaserCards.Count > 0;

    public string RenderTeasersLabel => TeaserCards.Count(t => t.Keep) is var n && n == 1 ? "Render 1 teaser" : $"Render {n} teasers";

    /// <summary>
    /// Asks for three teasers cut from the episode as it is now. One quick request to the fast model, its use logged
    /// like every other; the stand-in makes them by rule. Clips' voices not yet recorded are recorded first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRender))]
    private Task SuggestTeasersAsync() => RunActivityAsync("Suggesting teasers", async ct =>
    {
        var (episode, folder) = (Episode!, EpisodeFolder!);
        ReportActivity("Measuring the clips", 0.1);
        var lengths = await Task.Run(() => _builder.ClipLengthsAsync(episode, folder, ct), ct);
        if (lengths.Values.Sum(l => l.TotalSeconds) < Teasers.Target.TotalSeconds)
            throw new InvalidOperationException("The episode runs under ten seconds, so there is nothing to cut a teaser from.");
        ReportActivity("Choosing the moments", 0.5);
        var teasers = await _scriptWriter.SuggestTeasersAsync(episode, lengths, ct);
        SyncCards(); // the clips' lengths are now measured
        ShowTeasers(episode, teasers, lengths);
        StatusMessage = teasers.Count == 0 ? "No teaser could be cut from this episode." : $"{teasers.Count} teasers suggested. Change a hook or a moment, untick any you do not want, then render.";
    });

    private void ShowTeasers(Episode episode, IReadOnlyList<Teaser> teasers, IReadOnlyDictionary<Guid, TimeSpan>? measured = null)
    {
        // Each clip's length for the cards' strips: as measured for the suggestion, or as its card has it.
        IReadOnlyList<double> lengths = [.. episode.Clips.Select(c => measured?.GetValueOrDefault(c.Id) is { TotalSeconds: > 0 } real ? real.TotalSeconds
            : Clips.FirstOrDefault(card => card.Id == c.Id)?.Duration.TotalSeconds ?? 0)];
        foreach (var card in TeaserCards)
            card.PropertyChanged -= OnTeaserChanged;
        TeaserCards.Clear();
        foreach (var teaser in teasers)
        {
            var card = new TeaserViewModel(teaser, episode.Clips, lengths, Watch);
            card.PropertyChanged += OnTeaserChanged;
            TeaserCards.Add(card);
        }
        OnPropertyChanged(nameof(HasTeasers));
        OnPropertyChanged(nameof(RenderTeasersLabel));
        RenderTeasersCommand.NotifyCanExecuteChanged();
    }

    private void OnTeaserChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TeaserViewModel.Keep)) return;
        OnPropertyChanged(nameof(RenderTeasersLabel));
        RenderTeasersCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Plays one teaser as it stands on its card, small, before rendering it in full. Cut from the clips the quick
    /// preview draws, so after a preview it takes a moment, and a teaser not changed since plays at once. Costs nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRender))]
    private async Task WatchTeaserAsync(TeaserViewModel? card)
    {
        if (card is null || Episode is not { } episode) return;
        var (asked, captions, brand) = (card.ToTeaser(), Captions, SeriesBrand);
        card.IsBusy = true;
        try
        {
            await WatchAsync(card, async (folder, ct) =>
            {
                // Held to the rules first, as it is for the full render: a moment moved on its card may need trimming.
                var lengths = await _builder.ClipLengthsAsync(episode, folder, ct);
                var teaser = Teasers.Fit([asked], lengths) is [var fitted] ? fitted
                    : throw new InvalidOperationException("This teaser does not fit in ten seconds of its clips. Move a moment to a longer clip.");
                return await _builder.TeaserDraftAsync(episode, folder, teaser, captions, brand, ct);
            });
        }
        finally
        {
            card.IsBusy = false;
        }
    }

    private bool CanRenderTeasers => CanRender && TeaserCards.Any(t => t.Keep);

    /// <summary>Renders the kept teasers into the episode's export folder. Built here from the clips already made: it costs nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanRenderTeasers))]
    private Task RenderTeasersAsync() => RunActivityAsync("Rendering the teasers", async ct =>
    {
        var (episode, folder, captions, progress) = (Episode!, EpisodeFolder!, Captions, RenderProgress());
        var lengths = await Task.Run(() => _builder.ClipLengthsAsync(episode, folder, ct), ct);
        // Held to the rules again: a moment moved on its card may need trimming to fit its clip.
        var kept = TeaserCards.Where(t => t.Keep).Select(t => t.ToTeaser()).ToList();
        var teasers = Teasers.Fit(kept, lengths);
        if (teasers.Count == 0)
            throw new InvalidOperationException("None of the teasers kept fits in ten seconds of its clips. Move a moment to a longer clip, or suggest them again.");
        LastExport = await Task.Run(() => _builder.ExportTeasersAsync(episode, folder, teasers, captions, SeriesBrand, progress, ct), ct);
        SyncCards();
        var dropped = kept.Count - teasers.Count;
        StatusMessage = $"Saved {(teasers.Count == 1 ? "1 teaser" : $"{teasers.Count} teasers")} in export\\teasers."
            + (dropped == 1 ? " One could not be fitted to ten seconds and was left out." : dropped > 1 ? $" {dropped} could not be fitted to ten seconds and were left out." : "");
    });
}
