using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PoAutoRobo.App.ViewModels;

// The one long job in progress, turning whatever goes wrong into words for the user, and what has been said, kept to read back.
public partial class MainViewModel
{
    private readonly Stopwatch _activityClock = new();
    private CancellationTokenSource? _cancel;
    private (double From, double Span) _phase = (0, 1);

    /// <summary>
    /// For a job made of several jobs: the part now starting fills the bar from <paramref name="from"/> for
    /// <paramref name="span"/> of its length, so each part can go on reporting 0 to 1 and the bar still only moves forward.
    /// </summary>
    private void Phase(double from, double span) => _phase = (from, span);

    /// <summary>True while a script, a recording pass, a preview, a render or a batch of pictures is running. Only one runs at a time.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenderCommand), nameof(SuggestTeasersCommand), nameof(RenderTeasersCommand), nameof(BuildPreviewCommand), nameof(GenerateAllCommand), nameof(GeneratePictureCommand),
        nameof(NarrateAllCommand), nameof(CleanMediaCommand), nameof(CancelActivityCommand), nameof(CreateEpisodeCommand), nameof(AnotherTakeCommand), nameof(MakeDraftCommand),
        nameof(WatchClipCommand), nameof(WatchTeaserCommand), nameof(SuggestNoteCommand), nameof(ApplyNoteCommand))]
    public partial bool IsWorking { get; set; }

    /// <summary>What the job is, e.g. "Rendering the master video (1080p, 30 fps)".</summary>
    [ObservableProperty]
    public partial string ActivityTitle { get; set; } = "";

    /// <summary>The exact step it is on, e.g. "Drawing clip 7 of 16 · Rewards Shape Steps".</summary>
    [ObservableProperty]
    public partial string ActivityDetail { get; set; } = "";

    [ObservableProperty]
    public partial double ActivityPercent { get; set; }

    /// <summary>Time spent and a rough time left, worked out from the pace so far.</summary>
    [ObservableProperty]
    public partial string ActivityTime { get; set; } = "";

    /// <summary>
    /// Runs the one long job: shows it in the progress panel, lets Cancel stop it, and turns anything that goes
    /// wrong into a message instead of a crash.
    /// </summary>
    /// <returns>True when the work ran to the end; false when it was stopped or failed.</returns>
    private async Task<bool> RunActivityAsync(string title, Func<CancellationToken, Task> work, string? doneMessage = null)
    {
        ErrorMessage = null;
        StatusMessage = null;
        using var cancel = _cancel = new CancellationTokenSource();
        ActivityTitle = title;
        ActivityDetail = "Starting…";
        ActivityPercent = 0;
        ActivityTime = "";
        _phase = (0, 1);
        _activityClock.Restart();
        IsWorking = true;
        var finished = false;
        try
        {
            await work(cancel.Token);
            StatusMessage ??= doneMessage;
            finished = true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Stopped.";
        }
        catch (Exception e) // the top of a user action, and a job narrates, draws and reads files, so many things can fail
        {
            ErrorMessage = Plain(e);
        }
        finally
        {
            _activityClock.Stop();
            _cancel = null;
            IsWorking = false;
            foreach (var card in Clips)
                card.Progress = 0; // no card is left showing a half-finished ring
        }
        JobFinished(title, finished);
        return finished;
    }

    /// <summary>Runs a short user action; anything that goes wrong is shown, never left to crash the app.</summary>
    private async Task Guard(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user, or overtaken by a newer request: nothing to report.
        }
        catch (Exception e)
        {
            ErrorMessage = Plain(e);
        }
    }

    /// <summary>
    /// What went wrong, in words fit to show. The app's own errors are already written for the user; a failure from
    /// underneath (the network, the system) is replaced or cut to its first line, which is the part that says what happened.
    /// </summary>
    public static string Plain(Exception e) => e switch
    {
        HttpRequestException => "The service could not be reached. Check your connection and try again.",
        System.ComponentModel.Win32Exception => "A program the app needs could not be started. Check that FFmpeg and ffprobe are installed, then restart the app.",
        InvalidOperationException or InvalidDataException or ArgumentException or IOException or FfmpegException => e.Message,
        _ => e.Message.Split('\n')[0].Trim(),
    };

    private void ReportActivity(string detail, double fraction)
    {
        if (!IsWorking) return; // a late report after the job ended
        fraction = _phase.From + _phase.Span * Math.Clamp(fraction, 0, 1);
        ActivityDetail = detail;
        ActivityPercent = Math.Clamp(fraction, 0, 1) * 100;
        var elapsed = _activityClock.Elapsed;
        var left = fraction > 0.03 ? TimeSpan.FromTicks((long)(elapsed.Ticks * (1 - fraction) / fraction)) : (TimeSpan?)null;
        ActivityTime = left is { } remaining
            ? $"{elapsed:m\\:ss} so far · {(remaining.TotalMinutes >= 1 ? $"about {Math.Ceiling(remaining.TotalMinutes):0} min" : "under a minute")} left"
            : $"{elapsed:m\\:ss} so far";
    }

    /// <summary>Stops whichever long job is running. Finished pictures and narration are kept.</summary>
    [RelayCommand(CanExecute = nameof(IsWorking))]
    private void CancelActivity()
    {
        ActivityDetail = "Stopping…";
        _cancel?.Cancel();
    }

    // ---- What has been said, kept to read back ----

    private const int MessagesKept = 20;

    /// <summary>The last things the app said, newest first, each with the time. The message bar shows only the latest.</summary>
    public ObservableCollection<string> Messages { get; } = [];

    partial void OnErrorMessageChanged(string? value) => Remember(value);

    partial void OnStatusMessageChanged(string? value) => Remember(value);

    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private string? _lastNote;

    /// <summary>
    /// Something out of the ordinary in how a script or a picture came to be made: a clip left for the user to
    /// write, a picture made another way. Shown while the job runs and kept in the messages. Safe to call from any thread.
    /// </summary>
    public void Note(string message)
    {
        if (_ui is null || _ui.HasThreadAccess) Say();
        else _ui.TryEnqueue(Say);

        void Say()
        {
            Remember(message);
            _lastNote = message;
            if (IsWorking) ActivityDetail = message;
        }
    }

    private void Remember(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        Messages.Insert(0, $"{DateTime.Now:HH:mm} · {message}");
        while (Messages.Count > MessagesKept)
            Messages.RemoveAt(Messages.Count - 1);
        OnPropertyChanged(nameof(Notice));
    }

    /// <summary>The user closed the message bar. Clearing both means the same message, said again, shows again.</summary>
    public void DismissMessages() => (ErrorMessage, StatusMessage) = (null, null);
}
