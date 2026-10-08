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

    private readonly EpisodeBuilder _builder;

    public MainViewModel(IScriptWriter scriptWriter, EpisodeBuilder builder)
    {
        _scriptWriter = scriptWriter;
        _builder = builder;
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
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(AuditionCommand))]
    public partial ClipViewModel? SelectedClip { get; set; }

    /// <summary>The dialogue box's text while it is being typed; applied to the clip when the box loses focus.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftStats))]
    public partial string DraftDialogue { get; set; } = "";

    public bool HasSelection => SelectedClip is not null;

    public string DraftStats =>
        $"{DraftDialogue.Length} characters · about {Durations.Estimate(DraftDialogue).TotalSeconds:0} s";

    /// <summary>Set by the view that owns the audio player.</summary>
    public Action<string>? PlayAudio { get; set; }

    partial void OnSelectedClipChanged(ClipViewModel? value) => DraftDialogue = value?.Dialogue ?? "";

    [RelayCommand]
    private async Task ApplyDialogueAsync(CancellationToken ct)
    {
        if (Episode is null || SelectedClip is not { } clip || DraftDialogue.Trim() == clip.Dialogue) return;
        try
        {
            var updated = await EpisodeEditor.EditDialogueAsync(Episode, clip.Id, DraftDialogue, _scriptWriter, ct);
            Edit(_ => updated);
            await _builder.NarrateClipAsync(clip.Clip, EpisodeFolder!, ct); // new words are spoken straight away
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ErrorMessage = e.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task AuditionAsync(CancellationToken ct)
    {
        try
        {
            await ApplyDialogueAsync(ct);
            var narration = await _builder.NarrateClipAsync(SelectedClip!.Clip, EpisodeFolder!, ct);
            PlayAudio?.Invoke(narration.AudioPath);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ErrorMessage = e.Message;
        }
    }

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
