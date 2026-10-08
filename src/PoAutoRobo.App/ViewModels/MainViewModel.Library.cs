using System.Collections.ObjectModel;

namespace PoAutoRobo.App.ViewModels;

// Saved episodes: listing, opening, copying and deleting them.
public partial class MainViewModel
{
    public ObservableCollection<EpisodeSummary> Library { get; } = [];

    public bool HasLibrary => Library.Count > 0;

    private int _libraryRequest;

    /// <summary>Lists the saved episodes again. Read off the UI thread: it opens every episode's file, often in a synced folder.</summary>
    public async Task RefreshLibraryAsync()
    {
        var request = ++_libraryRequest;
        var summaries = await Task.Run(() => ProjectStore.Summaries(AppPaths.Episodes));
        if (request != _libraryRequest) return; // a newer listing is on its way
        Library.Clear();
        foreach (var summary in summaries)
            Library.Add(summary);
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
                if (Confirm is null || !await Confirm("This episode's file is damaged", "Restore it from the copy saved just before the last change?", "Restore"))
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
        if (Confirm is null || !await Confirm($"Delete {summary.Title}?", "The episode's folder, with its script, voices, pictures and finished videos, goes to the Recycle Bin.", "Delete"))
            return;
        if (isOpen)
        {
            _unsaved = false; // nothing of it is worth saving now
            ReleasePreview?.Invoke();
            Preview = null;
            _syncingClips = true;
            Clips.Clear();
            _syncingClips = false;
            SelectedClip = null;
            Episode = null;
            EpisodeFolder = null;
            ForgetHistory();
            Step = 0;
        }
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(summary.Folder,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        await RefreshLibraryAsync();
        StatusMessage = $"Moved {summary.Title} to the Recycle Bin.";
    });
}
