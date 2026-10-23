using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PoAutoRobo.App.ViewModels;

// Pictures and the host. Pictures are the one thing here with a known price, so every way of
// making one is explicit, and no two of them may run at once.
public partial class MainViewModel
{
    public bool PicturesAvailable => _visualsFor is not null;

    public ObservableCollection<string> HostCandidates { get; } = [];

    private Quality Quality => DraftPictures ? Quality.Low : Quality.Medium;

    /// <summary>The open episode's picture style, given it by the series as it was made or by Use the series look.</summary>
    private Look Look => Episode?.Look ?? Look.Comic;

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

    /// <summary>What every episode has spent this calendar month, and the budget it is held to (0 for none).</summary>
    public (decimal Spent, decimal Budget) MonthSpend() => (SpendLog.MonthTotal(AppPaths.Episodes, DateTimeOffset.Now), (decimal)MonthlyBudget);

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
        if (!await ConfirmSpend("Generate pictures?", $"{estimate.Summary}{kind} Pictures you already have are reused at no cost.", "Generate", estimate.Dollars ?? 0) || !CanGenerateAll)
            return;

        (int Made, List<string> Failures, string? Halted) drawn = (0, [], null);
        var finished = await RunActivityAsync("Generating pictures", async ct => drawn = await DrawAllAsync(estimate, ct));
        // A clip that fails is recorded and never stops the batch, so "not finished" here means stopped by the user.
        StatusMessage = finished ? $"Pictures are ready for {drawn.Made} clips. {drawn.Halted}".TrimEnd() : $"Stopped. Pictures are ready for {drawn.Made} clips.";
        ReportFailures(drawn.Failures);
    }

    /// <summary>Clips drawn at once. The picture service takes ten to thirty seconds a picture, nearly all of it waiting.</summary>
    private const int PicturesAtOnce = 3;

    /// <summary>
    /// Draws every clip the estimate names, a few at a time. A clip that fails is noted and the rest carry on. No clip
    /// is started that would take the spending (what is spent so far and what the clips being drawn will cost) past
    /// the cost the user agreed to, or past the monthly budget: a substitute or a retry can cost more than was shown,
    /// and agreeing to one figure is not agreeing to more.
    /// </summary>
    /// <returns>Halted: why the batch stopped short, said for the user; null when it drew every clip it was asked for.</returns>
    private async Task<(int Made, List<string> Failures, string? Halted)> DrawAllAsync(CostEstimate estimate, CancellationToken ct)
    {
        var failures = new List<string>();
        var made = 0;
        var started = 0;
        var total = estimate.ClipIds.Count;
        var before = PictureSpending();
        var drawing = 0m; // what the clips being drawn now will cost, not yet in the spend log
        string? halted = null;
        // Each piece carries on on the window's thread, so the counts and the episode are only ever changed there.
        await Batch.EachAsync(estimate.ClipIds, PicturesAtOnce, async id =>
        {
            ct.ThrowIfCancellationRequested();
            if (halted is not null) return;
            var clip = Episode?.Clips.FirstOrDefault(c => c.Id == id);
            var price = clip is null ? 0 : CostEstimate.PriceOf(clip, _imageModel, Quality);
            if (!estimate.Allows(PictureSpending() - before + drawing, price))
            {
                halted = $": the next picture would have taken the spending past the {SpendLog.Money(estimate.Dollars ?? 0)} you agreed to. Generate pictures again to see what the rest cost.";
                return;
            }
            if (OverBudget(price + drawing) is { } refusal)
            {
                halted = ". " + refusal;
                return;
            }
            started++;
            drawing += price;
            ReportActivity($"Drawing {clip?.Title} · {made + failures.Count} of {total} done", (double)(made + failures.Count) / total);
            string? failure;
            try
            {
                failure = await GenerateOneAsync(id, false, ct);
            }
            finally
            {
                drawing -= price;
            }
            if (failure is not null) failures.Add($"{clip?.Title}: {failure}"); else made++;
            ReportActivity($"Drew {clip?.Title} · {made + failures.Count} of {total} done", (double)(made + failures.Count) / total);
        }, ct);
        if (halted is null) return (made, failures, null);
        var rest = total - started == 1 ? "The last clip keeps its title card" : $"The last {total - started} clips keep their title cards";
        return (made, failures, rest + halted);
    }

    /// <summary>What this episode has spent on pictures and AI videos, the spending a batch of pictures is held to.</summary>
    private decimal PictureSpending() => EpisodeFolder is { } folder
        ? SpendLog.Breakdown(folder).Where(line => line.What is SpendLog.Picture or SpendLog.AiVideo).Sum(line => line.Dollars)
        : 0;

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
            ErrorMessage = OverBudget(CostEstimate.PriceOf(card.Clip, _imageModel, Quality)) ?? await GenerateOneAsync(card.Id, anotherTake, ct);
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
        var asked = DateTime.UtcNow;
        try
        {
            // When the picture service will not draw the clip as described, it is given a picture it will, and told so.
            var (drawn, paths, change) = await CurrentVisuals!.DrawOrSubstituteAsync(clip, Look, ct);
            // Footage and animations have no picture of their own; a frame is taken to stand for them on the card.
            if (FfmpegAvailable)
                foreach (var video in paths.Where(path => EpisodeBuilder.VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && !File.Exists(Clip.PosterFor(path))))
                    await _builder.PosterAsync(video, ct);
            await CheckHostAsync(drawn, paths, asked, ct);
            // Attached to the clip as it is now: footage, a new picture type or changed words since the request win.
            if (change is null)
            {
                Edit(e => EpisodeEditor.ApplyPicture(e, clip, paths));
            }
            else
            {
                Edit(e => EpisodeEditor.ApplySubstitute(e, clip, drawn, paths));
                Note(change);
            }
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

    /// <summary>
    /// Says when a new picture of the host has drifted from its locked design, so another take can be asked for. Only
    /// for pictures really made just now: one found already made was looked at when it was made.
    /// </summary>
    private async Task CheckHostAsync(Clip clip, IReadOnlyList<string> paths, DateTime asked, CancellationToken ct)
    {
        if (!clip.HostVisible || clip.Visual.Kind is not (VisualKind.Still or VisualKind.MultiPanel) || !File.Exists(AppPaths.HostSheet)
            || paths.FirstOrDefault() is not { } picture || File.GetCreationTimeUtc(picture) < asked)
            return;
        try
        {
            if (await _scriptWriter.CheckHostAsync(picture, AppPaths.HostSheet, ct) is { } difference)
                Note($"The host in “{clip.Title}” may not match its design: {difference} Another take may put it right.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A help on top of the picture, which is made either way.
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
            HostCandidates.Clear();
            HasHostDesign = File.Exists(AppPaths.HostSheet);
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
    }
}
