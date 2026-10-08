using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;

namespace PoAutoRobo.App.ViewModels;

// Pictures, the host and the visual mix. Pictures are the one thing here with a known price, so every way of
// making one is explicit, and no two of them may run at once.
public partial class MainViewModel
{
    public bool PicturesAvailable => _visualsFor is not null;

    public ObservableCollection<string> HostCandidates { get; } = [];

    private string Quality => DraftPictures ? "low" : "medium";

    private Visuals? CurrentVisuals => _visualsFor?.Invoke(EpisodeFolder ?? AppPaths.Host, Quality);

    /// <summary>What making the host candidates costs, said before they are made.</summary>
    public string HostCandidatesCost => CostEstimate.PriceOf(_imageModel, Quality) is { } each
        ? $"Making candidates creates 3 pictures (about ${(3 * each).ToString("0.00", CultureInfo.InvariantCulture)})."
        : "Making candidates creates 3 pictures. The cost depends on your Azure pricing.";

    // Not while one picture is being drawn: the batch may ask for that very clip, and the same request sent twice is paid for twice.
    private bool CanGenerateAll => HasEpisode && PicturesAvailable && !IsWorking && !GeneratePictureCommand.IsRunning;

    // Not during a batch, for the same reason.
    private bool CanGeneratePicture => HasSelection && PicturesAvailable && !IsWorking;

    /// <summary>Pictures are only ever made on an explicit request, and a batch only after the cost is confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerateAll))]
    private async Task GenerateAllAsync()
    {
        var estimate = CostEstimate.For(Episode!, _imageModel, Quality);
        ErrorMessage = null;
        if (estimate.NothingToDo)
        {
            StatusMessage = estimate.Summary;
            return;
        }
        var kind = DraftPictures ? " These are draft-quality pictures." : "";
        if (Confirm is null || !await Confirm("Generate pictures?", $"{estimate.Summary}{kind} Pictures you already have are reused at no cost.", "Generate") || !CanGenerateAll)
            return;

        var failures = new List<string>();
        var made = 0;
        var total = estimate.ClipIds.Count;
        var finished = await RunActivityAsync("Generating pictures", async ct =>
        {
            foreach (var id in estimate.ClipIds)
            {
                ct.ThrowIfCancellationRequested();
                var done = made + failures.Count;
                var title = Episode?.Clips.FirstOrDefault(c => c.Id == id)?.Title ?? "";
                ReportActivity($"Drawing the picture for clip {done + 1} of {total} · {title}", (double)done / total);
                if (await GenerateOneAsync(id, ct) is { } failure) failures.Add(failure); else made++;
                ClipDone?.Invoke((double)done / Math.Max(1, total - 1));
            }
        });
        // A clip that fails is recorded above and never stops the batch, so "not finished" here means stopped by the user.
        StatusMessage = finished ? $"Pictures are ready for {made} clips." : $"Stopped. Pictures are ready for {made} clips.";
        if (failures.Count > 0)
            ErrorMessage = $"{failures.Count} clips kept their title card. {failures[0]}";
    }

    [RelayCommand(CanExecute = nameof(CanGeneratePicture))]
    private Task GeneratePictureAsync(CancellationToken ct)
    {
        ErrorMessage = null;
        return Guard(async () =>
        {
            if (SelectedClip is { } card && await GenerateOneAsync(card.Id, ct) is { } failure)
                ErrorMessage = failure;
        });
    }

    /// <returns>Null on success, otherwise why this clip has no picture.</returns>
    /// <exception cref="OperationCanceledException">The user stopped it.</exception>
    private async Task<string?> GenerateOneAsync(Guid clipId, CancellationToken ct)
    {
        if (Episode?.Clips.FirstOrDefault(c => c.Id == clipId) is not { } clip)
            return "This clip is no longer in the episode.";
        var card = Clips.FirstOrDefault(c => c.Id == clipId);
        card?.IsBusy = true;
        try
        {
            var paths = await CurrentVisuals!.DrawAsync(clip, ct);
            // Attached to the clip as it is now: footage, a new picture type or changed words since the request win.
            Edit(e => EpisodeEditor.ApplyPicture(e, clip, paths));
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return "The picture service took too long to answer."; // a timeout, not the user: this clip was not made
        }
        catch (Exception e)
        {
            return Plain(e);
        }
        finally
        {
            card?.IsBusy = false;
            RefreshMeta(); // a new picture adds to what has been spent
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private Task MakeHostCandidatesAsync(CancellationToken ct)
    {
        ErrorMessage = null;
        return Guard(async () =>
        {
            var candidates = await CurrentVisuals!.CandidateSheetsAsync(3, ct);
            HostCandidates.Clear();
            foreach (var path in candidates)
                HostCandidates.Add(path);
        });
    }

    /// <summary>Locks a picture (a candidate or the user's own file) as the host for every episode.</summary>
    public void LockHost(string path)
    {
        try
        {
            CurrentVisuals?.LockSheet(path);
            StatusMessage = "Host locked. New pictures will use this character.";
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
    }

    // ---- Visual mix ----

    public MixPercentages Mix => Episode?.Mix ?? MixPercentages.Default;

    public void ApplyMix(MixPercentages mix) => Edit(e => EpisodeEditor.SetMix(e, mix));

    public void RerollMix() => Edit(e => EpisodeEditor.RerollMix(e, Random.Shared.Next()));
}
