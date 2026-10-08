using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PoAutoRobo.App.ViewModels;

// Every change to the episode goes through Edit here: it is saved, it can be undone, and the cards follow it.
public partial class MainViewModel
{
    private readonly EditHistory<Episode> _history = new();

    /// <summary>Applies one edit, saves it, and refreshes only the cards whose clip changed.</summary>
    public void Edit(Func<Episode, Episode> change)
    {
        if (Episode is not { } before || EpisodeFolder is null) return;
        try
        {
            var updated = change(before);
            if (ReferenceEquals(updated, before)) return;
            // Dragging a caption slider is dozens of edits in a row; they are undone as one.
            var captionsOnly = updated with { Captions = before.Captions } == before;
            _history.Record(before, merges: captionsOnly);
            Apply(updated, captionsOnly);
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            ErrorMessage = e.Message;
        }
    }

    private bool CanUndo => _history.CanUndo;

    private bool CanRedo => _history.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (Episode is { } now && _history.TryUndo(now, out var before)) Travel(before);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (Episode is { } now && _history.TryRedo(now, out var after)) Travel(after);
    }

    private void Travel(Episode target)
    {
        try
        {
            Apply(target);
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
    }

    private void ForgetHistory()
    {
        _history.Clear();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    // ---- Saving ----

    private bool _unsaved;
    private int _saveRequest;

    /// <summary>Writes a caption change that is still waiting to be saved. The window calls this as it closes.</summary>
    public void Flush()
    {
        if (!_unsaved || Episode is not { } episode || EpisodeFolder is not { } folder) return;
        _unsaved = false;
        try
        {
            ProjectStore.Save(episode, folder);
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
    }

    // A slider drag is dozens of caption changes a second; the file is written once the dragging pauses.
    private async void SaveSoon()
    {
        _unsaved = true;
        var request = ++_saveRequest;
        await Task.Delay(300);
        if (request == _saveRequest) Flush();
    }

    /// <summary>Makes <paramref name="updated"/> the episode: saves it and brings the cards into line, order included.</summary>
    /// <param name="captionsOnly">Only the caption look changed. No card shows it, so the cards are left alone.</param>
    private void Apply(Episode updated, bool captionsOnly = false)
    {
        if (captionsOnly)
        {
            Episode = updated;
            SaveSoon();
        }
        else
        {
            _saveRequest++; // this save carries any caption change that was still waiting
            _unsaved = false;
            ProjectStore.Save(updated, EpisodeFolder!);
            Episode = updated;
            SyncDeck(updated);
        }
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Brings the deck into line with the episode: cards are moved, added and removed to match its clips.</summary>
    private void SyncDeck(Episode updated)
    {
        var shownDialogue = SelectedClip?.Dialogue;
        var selected = SelectedClip;
        var selectedAt = selected is null ? 0 : Clips.IndexOf(selected);

        _syncingClips = true;
        for (var i = 0; i < updated.Clips.Count; i++)
        {
            var at = i;
            while (at < Clips.Count && Clips[at].Id != updated.Clips[i].Id) at++;
            if (at == Clips.Count)
            {
                Clips.Insert(i, NewCard(updated.Clips[i])); // a clip added, or brought back by undo
                continue;
            }
            if (at != i) Clips.Move(at, i);
            Clips[i].Clip = updated.Clips[i];
        }
        while (Clips.Count > updated.Clips.Count)
            Clips.RemoveAt(Clips.Count - 1); // what is left over is no longer in the episode
        _syncingClips = false;

        // A move can drop the selection in the list control, and a removed clip hands it to its neighbour.
        if (selected is null || !Clips.Contains(selected))
            selected = Clips.ElementAtOrDefault(Math.Clamp(selectedAt, 0, Clips.Count - 1));
        if (SelectedClip != selected) SelectedClip = selected;

        SyncCards();
        // The dialogue box holds a copy of the selected clip's words. When an edit changes those words (a depth
        // switch, fitted footage), refresh the copy, or leaving the box would write the old words over the new.
        if (SelectedClip is { } card && card.Dialogue != shownDialogue)
            DraftDialogue = card.Dialogue;
    }

    /// <summary>Renumbers the cards and refreshes what they show beyond the clip itself: recorded length and unchecked figures.</summary>
    private void SyncCards()
    {
        if (Episode is null || EpisodeFolder is null) return;
        // A repository's own name and file paths count as sourced, as does anything the user wrote in the topic.
        // An any-topic episode has no passages, so its figures are checked against the topic text alone.
        var sources = Snippets.SelectMany(s => new[] { s.Text, s.Repo, s.Path }).Append(Episode.Topic).ToList();
        for (var i = 0; i < Clips.Count; i++)
        {
            var card = Clips[i];
            card.Number = i + 1;
            card.Measured = _builder.MeasuredDuration(card.Clip, EpisodeFolder);
            card.Unverified = Grounding.UnverifiedClaims(card.Dialogue, sources) is { Count: > 0 } claims
                ? "Not in the sources: " + string.Join(", ", claims)
                : "";
        }
        RefreshMeta();
    }

    // A drag-reorder arrives as a remove followed by an insert; act once the deck is whole again.
    private void OnClipsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_syncingClips || Episode is null || Clips.Count != Episode.Clips.Count) return;
        Edit(episode => EpisodeEditor.Reorder(episode, [.. Clips.Select(c => c.Id)]));
    }

    // Reordering without a drag: for keyboard and screen-reader use, and as a dependable alternative to dragging.
    // Moving the card raises the same collection change a drag does, so the one reorder path handles both.
    [RelayCommand]
    private void MoveEarlier() => MoveSelected(-1);

    [RelayCommand]
    private void MoveLater() => MoveSelected(1);

    private void MoveSelected(int by)
    {
        if (SelectedClip is not { } card) return;
        var from = Clips.IndexOf(card);
        var to = from + by;
        if (from < 0 || to < 0 || to >= Clips.Count) return;
        Clips.Move(from, to);
        SelectedClip = card; // a move can drop the selection in the list control
    }

    // ---- Adding, copying, removing and naming clips ----

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddClip() => InsertAfterSelected(EpisodeEditor.NewClip());

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DuplicateClip() => InsertAfterSelected(EpisodeEditor.Copy(SelectedClip!.Clip));

    private void InsertAfterSelected(Clip clip)
    {
        var after = SelectedClip!.Id;
        Edit(e => EpisodeEditor.AddClip(e, after, clip));
        SelectedClip = Clips.FirstOrDefault(c => c.Id == clip.Id) ?? SelectedClip;
    }

    /// <summary>Takes the selected clip out of the episode. Undo brings it back, so nothing is asked.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveClip()
    {
        var id = SelectedClip!.Id;
        Edit(e => EpisodeEditor.RemoveClip(e, id));
    }

    public void RenameSelectedClip(string title)
    {
        if (SelectedClip is { } card && title.Trim() is { Length: > 0 } wanted && wanted != card.Title)
            Edit(e => EpisodeEditor.SetClipTitle(e, card.Id, wanted));
    }

    /// <summary>Changes what the selected clip's picture should show.</summary>
    public void SetSelectedVisualPrompt(string prompt)
    {
        if (SelectedClip is { } card && prompt.Trim() is { Length: > 0 } wanted && wanted != card.VisualPrompt)
            Edit(e => EpisodeEditor.SetVisualPrompt(e, card.Id, wanted));
    }

    /// <summary>Switches a clip's depth. A depth the clip does not have yet is written first, which takes a few seconds.</summary>
    private async void PickTier(ClipViewModel card, Tier tier)
    {
        if (Episode is not { } episode) return;
        if (card.Clip.Scripts.ContainsKey(tier))
        {
            Edit(e => EpisodeEditor.SetTier(e, card.Id, tier));
            return;
        }
        card.IsBusy = true;
        await Guard(async () =>
        {
            var script = await _scriptWriter.WriteTierAsync(episode.Topic, episode.Subject, card.Clip, tier, CancellationToken.None);
            // The episode may have been closed or swapped while the depth was being written.
            Edit(e => e.Clips.Any(c => c.Id == card.Id) ? EpisodeEditor.SetTier(EpisodeEditor.AddTier(e, card.Id, tier, script), card.Id, tier) : e);
        });
        card.IsBusy = false;
        card.ResetTierSelector();
    }

    // ---- Selected clip ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(DraftProblem))]
    [NotifyCanExecuteChangedFor(nameof(AuditionCommand), nameof(GeneratePictureCommand), nameof(AddClipCommand), nameof(DuplicateClipCommand), nameof(RemoveClipCommand))]
    public partial ClipViewModel? SelectedClip { get; set; }

    /// <summary>The dialogue box's text while it is being typed; applied to the clip when the box loses focus.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftStats), nameof(DraftProblem))]
    public partial string DraftDialogue { get; set; } = "";

    public bool HasSelection => SelectedClip is not null;

    public string DraftStats =>
        $"{DraftDialogue.Length} characters · about {Durations.Estimate(DraftDialogue).TotalSeconds:0} s";

    /// <summary>What is wrong with the words as typed, shown under the box while typing; empty when they are fine.</summary>
    public string DraftProblem =>
        !HasSelection ? "" : DraftDialogue.Trim().Length == 0 ? "Dialogue cannot be empty. The clip keeps its last words." : Durations.Warning(DraftDialogue) ?? "";

    partial void OnSelectedClipChanged(ClipViewModel? value) => DraftDialogue = value?.Dialogue ?? "";

    // Selecting another clip reloads the dialogue box, and the click that selects it arrives before the box reports
    // losing focus. Words typed for the clip being left are saved here first, or they would be thrown away.
    partial void OnSelectedClipChanging(ClipViewModel? oldValue, ClipViewModel? newValue)
    {
        if (!_syncingClips && oldValue is not null && Episode is not null && Episode.Clips.Any(c => c.Id == oldValue.Id))
            _ = ApplyDialogueToAsync(oldValue, DraftDialogue, CancellationToken.None);
    }

    [RelayCommand]
    private Task ApplyDialogueAsync(CancellationToken ct) =>
        SelectedClip is { } clip ? ApplyDialogueToAsync(clip, DraftDialogue, ct) : Task.CompletedTask;

    private Task ApplyDialogueToAsync(ClipViewModel clip, string draft, CancellationToken ct) => Guard(async () =>
    {
        // Empty words are not applied; the box says so underneath.
        if (Episode is null || EpisodeFolder is not { } folder || draft.Trim().Length == 0 || draft.Trim() == clip.Dialogue) return;
        var updated = await EpisodeEditor.EditDialogueAsync(Episode, clip.Id, draft, _scriptWriter, ct);
        // The drift check above can take a moment; apply only this clip so edits made meanwhile are kept.
        var changed = updated.Clips.First(c => c.Id == clip.Id);
        Edit(e => EpisodeEditor.ReplaceClip(e, changed));
        await _builder.NarrateClipAsync(changed, folder, ct); // new words are spoken straight away
        SyncCards();
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task AuditionAsync(CancellationToken ct) => Guard(async () =>
    {
        await ApplyDialogueAsync(ct);
        var narration = await _builder.NarrateClipAsync(SelectedClip!.Clip, EpisodeFolder!, ct);
        SyncCards();
        PlayAudio?.Invoke(narration.AudioPath);
    });

    private bool CanStartJob => HasEpisode && !IsWorking;

    /// <summary>Records every clip's voice now, so the cards and the running time show real lengths before any render.</summary>
    [RelayCommand(CanExecute = nameof(CanStartJob))]
    private Task NarrateAllAsync() => RunActivityAsync("Recording the voices", async ct =>
    {
        var folder = EpisodeFolder!;
        var cards = Clips.ToList();
        for (var i = 0; i < cards.Count; i++)
        {
            ReportActivity($"Recording clip {i + 1} of {cards.Count} · {cards[i].Clip.Title}", (double)i / cards.Count);
            cards[i].IsBusy = true;
            try
            {
                await _builder.NarrateClipAsync(cards[i].Clip, folder, ct);
            }
            finally
            {
                cards[i].IsBusy = false;
            }
            SyncCards();
            ClipDone?.Invoke((double)i / Math.Max(1, cards.Count - 1));
        }
    }, "Voices recorded. Clip lengths and the running time are now measured, not estimated.");

    // ---- The user's own footage ----

    [RelayCommand]
    private Task AttachVideoAsync(string path)
    {
        if (Episode is null || SelectedClip is not { } card) return Task.CompletedTask;
        if (IsWorking || !FfmpegAvailable)
        {
            ErrorMessage = null; // said again after being closed, it must show again
            ErrorMessage = IsWorking
                ? "Wait for the job in progress to finish, or cancel it, before adding your video."
                : "FFmpeg is needed to read your video. Install it, then restart the app.";
            return Task.CompletedTask;
        }
        return RunActivityAsync("Fitting the narration to your video", async ct =>
        {
            var (clip, folder) = (card.Clip, EpisodeFolder!);
            var length = await _builder.ProbeDurationAsync(path, ct);
            if (length < Conformance.MinFootage || length > Conformance.MaxFootage)
                throw new InvalidOperationException("Footage must be between 5 seconds and 2 minutes long.");
            ReportActivity("Rewriting and re-recording the dialogue to match", 0.2);
            var fit = await Conformance.FitAsync(clip.Active.Dialogue, length, _scriptWriter,
                async (text, rate, c) => (await _builder.NarrateClipAsync(EpisodeEditor.WithDialogue(clip, text) with { NarrationRate = rate }, folder, c)).Duration,
                ct);

            // Named for the clip as well as the file, so two different videos called "take.mp4" cannot overwrite each other.
            ReportActivity("Copying your video into the episode", 0.9);
            var copy = Path.Combine(folder, "imports", $"{clip.Id:N}-{Path.GetFileName(path)}");
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            if (!string.Equals(Path.GetFullPath(path), copy, StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => File.Copy(path, copy, overwrite: true), ct); // can be hundreds of megabytes

            await _builder.PosterAsync(copy, ct); // a frame of the footage, so its card is not left blank
            Edit(e => EpisodeEditor.AttachVideo(e, clip.Id, copy, fit));
            StatusMessage = fit.WithinTolerance
                ? $"Video {length:m\\:ss} · narration fitted to {fit.Duration.TotalSeconds:0.0} s."
                : $"Closest fit is {Math.Abs(fit.Gap.TotalSeconds):0.0} s too {(fit.Gap > TimeSpan.Zero ? "short" : "long")}. Edit the dialogue to close the gap.";
        });
    }

    [RelayCommand]
    private void RemoveVideo()
    {
        if (SelectedClip is { } card)
            Edit(e => EpisodeEditor.RemoveVideo(e, card.Id));
    }
}
