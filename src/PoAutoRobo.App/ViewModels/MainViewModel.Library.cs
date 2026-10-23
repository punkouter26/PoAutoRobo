using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PoAutoRobo.App.ViewModels;

// Episodes: writing a new one, then listing, opening, copying, renaming, tidying and deleting the saved ones.
public partial class MainViewModel
{
    public ObservableCollection<EpisodeSummary> Library { get; } = [];

    public bool HasLibrary => Library.Count > 0;

    private int _libraryRequest;

    /// <summary>Lists the saved episodes again. Read off the UI thread: it opens every episode's file, often in a synced folder.</summary>
    private int _suggestedNumber = 1; // the episode number last filled in, so a number the user typed is left alone

    public async Task RefreshLibraryAsync()
    {
        var request = ++_libraryRequest;
        var summaries = await Task.Run(() => ProjectStore.Summaries(AppPaths.Episodes));
        if (request != _libraryRequest) return; // a newer listing is on its way
        Library.Clear();
        foreach (var summary in summaries)
            Library.Add(summary);
        // The next in the series, unless the user has typed a number of their own since it was last filled in.
        var next = ProjectStore.NextNumber(summaries);
        if (double.IsNaN(EpisodeNumber) || (int)EpisodeNumber == _suggestedNumber) EpisodeNumber = next;
        _suggestedNumber = next;
        OnPropertyChanged(nameof(HasLibrary));
    }

    /// <summary>Reopens a saved episode. Works with no connection: it is a plain file read.</summary>
    public async Task OpenEpisodeAsync(string folder)
    {
        ErrorMessage = null;
        if (IsWorking || !IsConnected)
        {
            ErrorMessage = IsWorking
                ? "Wait for the job in progress to finish, or cancel it, before opening another episode."
                : "Still connecting. Try again in a moment.";
            return;
        }
        try
        {
            Episode episode;
            try
            {
                episode = ProjectStore.Load(folder);
            }
            catch (InvalidDataException) when (File.Exists(Path.Combine(folder, ProjectStore.FileName + ".bak")))
            {
                if (!await Confirm("This episode's file is damaged", "Restore it from the copy saved just before the last change?", "Restore"))
                    return;
                episode = ProjectStore.RestoreBackup(folder);
            }
            Open(episode, folder, ProjectStore.LoadSnippets(folder));
            StatusMessage = $"Opened {episode.Title}.";
        }
        catch (InvalidDataException e)
        {
            ErrorMessage = e.Message;
        }
    }

    public Task DuplicateEpisodeAsync(EpisodeSummary summary) => Guard(async () =>
    {
        await Task.Run(() => ProjectStore.Duplicate(summary.Folder, AppPaths.Episodes)); // copies every picture and recording
        await RefreshLibraryAsync();
        StatusMessage = $"Copied {summary.Title}.";
    });

    /// <summary>Moves an episode folder to the Recycle Bin, after asking. The episode in use can only go when no job is running.</summary>
    public Task DeleteEpisodeAsync(EpisodeSummary summary) => Guard(async () =>
    {
        var isOpen = string.Equals(summary.Folder, EpisodeFolder, StringComparison.OrdinalIgnoreCase);
        if (isOpen && IsWorking)
        {
            ErrorMessage = null; // said again after being closed, it must show again
            ErrorMessage = "Wait for the job in progress to finish, or cancel it, before deleting this episode.";
            return;
        }
        if (!await Confirm($"Delete {summary.Title}?", "The episode's folder, with its script, voices, pictures and finished videos, goes to the Recycle Bin.", "Delete"))
            return;
        if (isOpen)
        {
            _unsaved = false; // nothing of it is worth saving now
            ReleasePreview();
            Preview = null;
            _syncingClips = true;
            Clips.Clear();
            _syncingClips = false;
            SelectedClip = null;
            Episode = null;
            EpisodeFolder = null;
            ForgetHistory();
            Step = LessonStep;
            SavePrefs(); // there is no longer an episode to come back to
        }
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(summary.Folder,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        await RefreshLibraryAsync();
        StatusMessage = $"Moved {summary.Title} to the Recycle Bin.";
    });

    /// <summary>True while a script is being written; the deck shows placeholder cards meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIntro), nameof(ShowTopic), nameof(ShowDeck))]
    public partial bool IsCreating { get; set; }

    /// <summary>One placeholder per clip the script being written may have: a clip's title once it is written, empty until then.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Skeletons { get; set; } = [];

    // Not while a long job runs: it works on the current episode and must not have it swapped from under it.
    private bool CanCreateEpisode => !string.IsNullOrWhiteSpace(TopicInput) && !IsWorking && IsConnected;

    [RelayCommand(CanExecute = nameof(CanCreateEpisode))]
    private Task CreateEpisodeAsync() => RunActivityAsync("Writing the script", async ct =>
    {
        var number = Math.Max(1, (int)Math.Round(double.IsNaN(EpisodeNumber) ? 1 : EpisodeNumber));
        // The series so far, so the script can build on it; read off the window's thread, as it opens every episode.
        var lesson = new Lesson(TopicInput, WentWrongInput, NextStructure, [.. Footage.Select(f => f.ToNote())])
        {
            Earlier = await Task.Run(() => ProjectStore.SeriesBefore(AppPaths.Episodes, number), ct),
        };
        var topic = lesson.Topic;
        var length = EpisodeLength.Standard;
        Skeletons = [.. Enumerable.Repeat("", length.MaxClips)];
        _lastNote = null;
        IsCreating = true;
        try
        {
            // The films first: what happens in each, and what is said in those that keep their own sound, is what
            // the script is written to. Each is kept by the film, so writing the episode again asks for neither.
            if (lesson.Footage.Count > 0)
            {
                ReportActivity("Watching your films", 0.01);
                lesson = lesson with { Footage = [.. await Task.WhenAll(lesson.Footage.Select(note => UnderstandAsync(note, ct)))] };
            }
            // Every episode is about the R1, so every script is grounded in its official repositories.
            IReadOnlyList<GroundingSnippet> grounding = [];
            if (_grounding is not null)
            {
                ReportActivity("Reading the official repositories", 0.02);
                grounding = await _grounding.FindAsync(topic, ct);
            }
            ReportActivity("Writing the script", 0.1);
            var of = length.MinClips == length.MaxClips ? $"{length.MaxClips}" : $"up to {length.MaxClips}";
            // Each finished clip's title lands on its placeholder card while the rest are still being written.
            var written = new Progress<IReadOnlyList<string>>(titles =>
            {
                if (!IsCreating) return;
                ReportActivity($"Written {titles.Count} of {of} clips", 0.1 + 0.9 * Math.Min(1.0, (double)titles.Count / length.MaxClips));
                Skeletons = [.. titles.Take(length.MaxClips), .. Enumerable.Repeat("", Math.Max(0, length.MaxClips - titles.Count))];
            });
            // The script chooses each clip's picture type; whatever cannot be made on this computer becomes a still.
            Func<VisualKind, bool> canMake = CurrentVisuals is { } visuals ? visuals.CanMake : _ => false;
            var episode = VisualMix.Settle(await _scriptWriter.WriteEpisodeAsync(lesson, grounding, length, ct, written), canMake) with { Number = number };
            var folder = ProjectStore.NewFolder(AppPaths.Episodes, episode.Title, number); // never on top of an earlier episode
            episode = await BringInFootageAsync(episode, lesson.Footage, folder, ct);
            // Every episode starts in the series look, with its own copy of the series music.
            var kit = SeriesKit.Load(AppPaths.Series);
            var music = SeriesKit.PathOf(AppPaths.Series, kit.Music) is { } track ? await ImportAsync(track, folder, "music-" + Path.GetFileName(track), ct) : null;
            episode = EpisodeEditor.UseSeriesLook(episode, kit, music);
            ProjectStore.Save(episode, folder);
            Footage.Clear(); // in the episode now; the next lesson starts with none
            ProjectStore.SaveSnippets(grounding, folder);
            lock (_usedBeforeFolder)
            {
                foreach (var (what, units, dollars) in _usedBeforeFolder)
                    SpendLog.Add(folder, what, dollars, units);
                _usedBeforeFolder.Clear();
            }
            Open(episode, folder, grounding);
            // What was said along the way (a clip left for the user to write) is what to read first, on the page it concerns.
            if (_lastNote is { } note) StatusMessage = note;
            _ = ReviewAsync(); // read by an editor while the user looks the clips over; nothing waits for it
        }
        finally
        {
            IsCreating = false;
        }
    });

    /// <summary>Makes an episode the one being worked on, and lands on its clips.</summary>
    private void Open(Episode episode, string folder, IReadOnlyList<GroundingSnippet> snippets)
    {
        Flush(); // a caption change to the episode being left may still be waiting to be saved
        _syncingClips = true;
        ReleasePreview();
        Preview = null;
        LastExport = null;
        ShowTeasers(episode, []); // those were cut from the episode being left
        ForgetHistory();
        _notes.Clear();
        EpisodeFolder = folder;
        Snippets.Clear();
        foreach (var snippet in snippets)
            Snippets.Add(snippet);
        OnPropertyChanged(nameof(SnippetsHeading));
        Episode = episode;
        Clips.Clear();
        foreach (var clip in episode.Clips)
            Clips.Add(NewCard(clip));
        _syncingClips = false;
        SyncCards();
        SelectedClip = Clips.FirstOrDefault();
        Step = ClipsStep;
        SavePrefs(); // this is now the episode to come back to
        _ = Guard(ShowFootageAsync);
    }

    [RelayCommand(CanExecute = nameof(HasEpisode))]
    private async Task RenameEpisodeAsync()
    {
        if (Episode is { } episode && await Ask("Rename episode", episode.Title) is { } title)
            Edit(e => EpisodeEditor.Rename(e, title));
    }

    // ---- Housekeeping ----

    /// <summary>Opens File Explorer on a folder, or on the folder holding a file with that file picked out.</summary>
    public static void ShowInExplorer(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\""))?.Dispose();

    /// <summary>Removes recordings of earlier wording and replaced pictures from the episode folder, after asking.</summary>
    [RelayCommand(CanExecute = nameof(CanStartJob))]
    private Task CleanMediaAsync() => Guard(async () =>
    {
        var unused = _builder.UnusedMedia(Episode!, EpisodeFolder!);
        if (unused.Count == 0)
        {
            StatusMessage = "Nothing to remove: every saved file is in use.";
            return;
        }
        var megabytes = unused.Sum(file => new FileInfo(file).Length) / 1048576.0;
        var message = $"{unused.Count} files ({megabytes:0.0} MB) are no longer used by any clip: recordings of earlier wording and replaced pictures. " +
            "They are deleted for good, and a removed picture would be charged again if you went back to it. Undo history is cleared.";
        if (!await Confirm("Remove unused media?", message, "Remove") || IsWorking)
            return;
        ForgetHistory(); // an undone edit could point at a file that is about to go
        foreach (var file in unused)
            File.Delete(file);
        StatusMessage = $"Removed {unused.Count} unused files ({megabytes:0.0} MB).";
    });
}
