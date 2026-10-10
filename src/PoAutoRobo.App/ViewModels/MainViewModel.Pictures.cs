using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PoAutoRobo.App.ViewModels;

// Pictures, the host and the visual mix. Pictures are the one thing here with a known price, so every way of
// making one is explicit, and no two of them may run at once.
public partial class MainViewModel
{
    public bool PicturesAvailable => _visualsFor is not null;

    public ObservableCollection<string> HostCandidates { get; } = [];

    private Quality Quality => DraftPictures ? Quality.Low : Quality.Medium;

    private Visuals? CurrentVisuals => _visualsFor?.Invoke(EpisodeFolder ?? AppPaths.Host, Quality);

    /// <summary>What making the host candidates costs, said before they are made.</summary>
    public string HostCandidatesCost => CostEstimate.PriceOf(_imageModel, Quality) is { } each
        ? $"Making candidates creates 3 pictures (about {SpendLog.Money(3 * each)})."
        : "Making candidates creates 3 pictures. The cost depends on your Azure pricing.";

    // ---- The monthly budget ----

    /// <summary>The most to spend in a calendar month across every episode, in dollars; 0 for no limit.</summary>
    [ObservableProperty]
    public partial double MonthlyBudget { get; set; }

    // An emptied box gives "not a number", which means no limit, as zero does.
    partial void OnMonthlyBudgetChanged(double value)
    {
        if (double.IsNaN(value) || value < 0) MonthlyBudget = 0;
    }

    /// <summary>Why <paramref name="more"/> dollars may not be spent now, or null when it may.</summary>
    private string? OverBudget(decimal more)
    {
        if (MonthlyBudget <= 0) return null;
        var spent = SpendLog.MonthTotal(AppPaths.Episodes, DateTimeOffset.Now);
        return spent + more > (decimal)MonthlyBudget
            ? $"That would pass this month's budget of {SpendLog.Money((decimal)MonthlyBudget)}: {SpendLog.Money(spent)} is spent already. Raise the budget in Settings to go on."
            : null;
    }

    /// <summary>This episode's spending by kind, then the month's total across every episode; for the Settings panel.</summary>
    [ObservableProperty]
    public partial string SpendDetail { get; set; } = "";

    /// <summary>Works out <see cref="SpendDetail"/> afresh. Called as the panel opens: it reads every episode's spending.</summary>
    public void RefreshSpendDetail()
    {
        var lines = EpisodeFolder is { } folder
            ? SpendLog.Breakdown(folder).Select(line => string.Join(" · ", new[] { line.What, line.Units > 0 ? $"{line.Units:N0}" : "", line.Dollars > 0 ? SpendLog.Money(line.Dollars) : "" }.Where(part => part.Length > 0))).ToList()
            : [];
        if (lines.Count == 0) lines.Add(EpisodeFolder is null ? "No episode is open." : "Nothing spent on this episode yet.");
        lines.Add($"This month, every episode: {SpendLog.Money(SpendLog.MonthTotal(AppPaths.Episodes, DateTimeOffset.Now))}");
        SpendDetail = string.Join('\n', lines);
    }

    // ---- Making pictures ----

    // Not while one picture is being drawn: the batch may ask for that very clip, and the same request sent twice is paid for twice.
    private bool CanGenerateAll => HasEpisode && PicturesAvailable && !IsWorking && !GeneratePictureCommand.IsRunning && !AnotherTakeCommand.IsRunning;

    // Not during a batch, for the same reason.
    private bool CanGeneratePicture => HasSelection && PicturesAvailable && !IsWorking && !AnotherTakeCommand.IsRunning;

    // Another take of something not yet made is just the first take.
    private bool CanTakeAnother => CanGeneratePicture && !GeneratePictureCommand.IsRunning && SelectedClip is { HasUserVideo: false, Clip.Visual.MediaPaths.Count: > 0 };

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
        if (OverBudget(estimate.Dollars ?? 0) is { } refusal)
        {
            ErrorMessage = refusal;
            return;
        }
        var kind = DraftPictures ? " These are draft-quality pictures." : "";
        if (!await Confirm("Generate pictures?", $"{estimate.Summary}{kind} Pictures you already have are reused at no cost.", "Generate") || !CanGenerateAll)
            return;

        (int Made, List<string> Failures) drawn = (0, []);
        var finished = await RunActivityAsync("Generating pictures", async ct => drawn = await DrawAllAsync(estimate, ct));
        // A clip that fails is recorded and never stops the batch, so "not finished" here means stopped by the user.
        StatusMessage = finished ? $"Pictures are ready for {drawn.Made} clips." : $"Stopped. Pictures are ready for {drawn.Made} clips.";
        ReportFailures(drawn.Failures);
    }

    /// <summary>Draws every clip the estimate names, one after another. A clip that fails is noted and the rest carry on.</summary>
    private async Task<(int Made, List<string> Failures)> DrawAllAsync(CostEstimate estimate, CancellationToken ct)
    {
        var failures = new List<string>();
        var made = 0;
        var total = estimate.ClipIds.Count;
        foreach (var id in estimate.ClipIds)
        {
            ct.ThrowIfCancellationRequested();
            var done = made + failures.Count;
            var title = Episode?.Clips.FirstOrDefault(c => c.Id == id)?.Title ?? "";
            ReportActivity($"Drawing the picture for clip {done + 1} of {total} · {title}", (double)done / total);
            if (await GenerateOneAsync(id, false, ct) is { } failure) failures.Add($"{title}: {failure}"); else made++;
            ClipDone((double)done / Math.Max(1, total - 1));
        }
        return (made, failures);
    }

    /// <summary>Says how many clips went without a picture and why the first did; every reason is kept in the message history.</summary>
    private void ReportFailures(List<string> failures)
    {
        if (failures.Count == 0) return;
        foreach (var failure in failures.Skip(1))
            Remember(failure);
        ErrorMessage = $"{failures.Count} clips kept their title card. {failures[0]}";
    }

    [RelayCommand(CanExecute = nameof(CanGeneratePicture))]
    private Task GeneratePictureAsync(CancellationToken ct) => DrawSelectedAsync(false, ct);

    /// <summary>A fresh attempt at the selected clip's picture. The one it has is kept beside it and can be gone back to.</summary>
    [RelayCommand(CanExecute = nameof(CanTakeAnother))]
    private Task AnotherTakeAsync(CancellationToken ct) => DrawSelectedAsync(true, ct);

    private Task DrawSelectedAsync(bool anotherTake, CancellationToken ct)
    {
        ErrorMessage = null;
        return Guard(async () =>
        {
            if (SelectedClip is not { } card) return;
            var price = Visuals.PictureCount(card.Clip.Visual.Kind) * (CostEstimate.PriceOf(_imageModel, Quality) ?? 0) + (card.Clip.Visual.Kind == VisualKind.AiVideo ? CostEstimate.VideoPrice : 0);
            ErrorMessage = OverBudget(price) ?? await GenerateOneAsync(card.Id, anotherTake, ct);
        });
    }

    /// <summary>Goes back to one of the selected clip's earlier takes. Free: the picture is already on disk.</summary>
    public void UseEarlierTake(int index)
    {
        if (SelectedClip is { } card)
            Edit(e => EpisodeEditor.UseEarlierTake(e, card.Id, index));
    }

    /// <returns>Null on success, otherwise why this clip has no picture.</returns>
    /// <exception cref="OperationCanceledException">The user stopped it.</exception>
    private async Task<string?> GenerateOneAsync(Guid clipId, bool anotherTake, CancellationToken ct)
    {
        if (Episode?.Clips.FirstOrDefault(c => c.Id == clipId) is not { } clip)
            return "This clip is no longer in the episode.";
        if (anotherTake) clip = EpisodeEditor.NextTake(clip);
        var card = Clips.FirstOrDefault(c => c.Id == clipId);
        card?.IsBusy = true;
        try
        {
            var paths = await CurrentVisuals!.DrawAsync(clip, Look, ct);
            // Footage and animations have no picture of their own; a frame is taken to stand for them on the card.
            if (FfmpegAvailable)
                foreach (var video in paths.Where(path => EpisodeBuilder.VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && !File.Exists(Clip.PosterFor(path))))
                    await _builder.PosterAsync(video, ct);
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

    public Look Look => Episode?.Look ?? Look.Comic;

    /// <summary>True when at least half the clips show the host; what the "host in every clip" switch starts on.</summary>
    public bool HostMostlyVisible => Episode is { } episode && episode.Clips.Count(c => c.HostVisible) * 2 >= episode.Clips.Count;

    public void ApplyLook(Look look) => Edit(e => EpisodeEditor.SetLook(e, look));

    public void ShowHostEverywhere(bool visible) => Edit(e => EpisodeEditor.SetHostEverywhere(e, visible));
}
