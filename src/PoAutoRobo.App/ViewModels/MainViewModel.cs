using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IScriptWriter _scriptWriter;
    private bool _syncingClips;

    public MainViewModel(IScriptWriter scriptWriter)
    {
        _scriptWriter = scriptWriter;
        Clips.CollectionChanged += OnClipsChanged;
    }

    public static string EpisodesRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PoAutoRobo");

    public ObservableCollection<ClipViewModel> Clips { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateEpisodeCommand))]
    public partial string TopicInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EpisodeTitle), nameof(RuntimeText), nameof(ClipCountText))]
    public partial Episode? Episode { get; set; }

    [ObservableProperty]
    public partial ClipViewModel? SelectedClip { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public string? EpisodeFolder { get; private set; }

    public string EpisodeTitle => Episode?.Title ?? "No episode yet";

    public string ClipCountText => Episode is null ? "" : $"{Episode.Clips.Count} clips";

    public string RuntimeText
    {
        get
        {
            if (Episode is null) return "";
            var total = TimeSpan.FromTicks(Episode.Clips.Sum(c => Durations.Estimate(c.Active.Dialogue).Ticks));
            return $"Runtime {total:m\\:ss} · target 3–10 min";
        }
    }

    private bool CanCreateEpisode => !string.IsNullOrWhiteSpace(TopicInput);

    [RelayCommand(CanExecute = nameof(CanCreateEpisode))]
    private async Task CreateEpisodeAsync(CancellationToken ct)
    {
        ErrorMessage = null;
        try
        {
            var episode = await _scriptWriter.WriteEpisodeAsync(TopicInput.Trim(), [], ct);
            EpisodeFolder = Path.Combine(EpisodesRoot, EpisodeBuilder.Slug(episode.Title));
            ProjectStore.Save(episode, EpisodeFolder);
            Show(episode);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ErrorMessage = e.Message;
        }
    }

    /// <summary>Applies one edit, saves it, and refreshes only the cards whose clip changed.</summary>
    public void Edit(Func<Episode, Episode> change)
    {
        if (Episode is null || EpisodeFolder is null) return;
        try
        {
            var updated = change(Episode);
            ProjectStore.Save(updated, EpisodeFolder);
            Episode = updated;
            foreach (var card in Clips)
                card.Clip = updated.Clips.First(c => c.Id == card.Id);
            Renumber();
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            ErrorMessage = e.Message;
        }
    }

    private void Show(Episode episode)
    {
        _syncingClips = true;
        Episode = episode;
        Clips.Clear();
        foreach (var clip in episode.Clips)
            Clips.Add(new ClipViewModel(clip, Edit));
        _syncingClips = false;
        Renumber();
        SelectedClip = Clips.FirstOrDefault();
    }

    // A drag-reorder arrives as a remove followed by an insert; act once the deck is whole again.
    private void OnClipsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_syncingClips || Episode is null || Clips.Count != Episode.Clips.Count) return;
        Edit(episode => EpisodeEditor.Reorder(episode, [.. Clips.Select(c => c.Id)]));
    }

    private void Renumber()
    {
        for (var i = 0; i < Clips.Count; i++)
            Clips[i].Number = i + 1;
    }
}
