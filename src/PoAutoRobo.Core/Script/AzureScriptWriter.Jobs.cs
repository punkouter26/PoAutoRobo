using System.Text;

namespace PoAutoRobo.Core.Script;

// Everything asked of the script service once the script is written: one clip at a time, the editor's read, teasers, upload notes, and looking at films and pictures.
public sealed partial class AzureScriptWriter
{
    public async Task<SaferPicture> RethinkPictureAsync(Clip clip, CancellationToken ct) =>
        await Ask<SaferPicture>(settings.FastChatDeployment, ScriptSchemas.RethinkSystem,
            Fenced("existing", " what=\"the picture that was declined\"", clip.Active.VisualPrompt) + Fenced("existing", " what=\"what the narrator says over it\"", clip.Active.Dialogue),
            "rethink", ScriptSchemas.Rethink, ct);

    public async Task<Scene> WriteSceneAsync(SceneRequest request, CancellationToken ct, Scene? failed = null, string? problem = null)
    {
        var prompt = new StringBuilder()
            .AppendLine(ScriptSchemas.SceneKind(request.Kind))
            .AppendLine(FormattableString.Invariant($"Length: {request.Length.TotalSeconds:0} seconds."))
            .AppendLine($"The look: {Visuals.LookInWords(request.Look)}.").AppendLine()
            .Append(Fenced("topic", " what=\"what the scene shows\"", request.Shows)).AppendLine()
            .Append(Fenced("existing", " what=\"the narration spoken over it\"", request.Narration));
        if (failed is not null)
            prompt.AppendLine()
                .Append(Fenced("existing", " what=\"the script you wrote last time, which did not come out right\"", failed.Script))
                .Append(Fenced("existing", " what=\"what was wrong with it\"", problem is { Length: > MaxErrorLength } ? problem[..MaxErrorLength] : problem ?? ""))
                .AppendLine("Write the whole scene again so that it runs and the fault is gone.");

        // Moving words and charts are near to a set pattern, which the fast model draws as well and far more cheaply.
        // A diagram is not; but when the main model is too busy or too slow to answer, the fast one's is better than none.
        var deployment = request.Kind is VisualKind.Chart or VisualKind.KineticText ? settings.FastChatDeployment : settings.ChatDeployment;
        try
        {
            return await Ask<Scene>(deployment, ScriptSchemas.SceneSystem, prompt.ToString(), "scene", ScriptSchemas.Scene, ct);
        }
        catch (Exception e) when (deployment != settings.FastChatDeployment && IsBusy(e, ct))
        {
            return await Ask<Scene>(settings.FastChatDeployment, ScriptSchemas.SceneSystem, prompt.ToString(), "scene", ScriptSchemas.Scene, ct);
        }
    }

    private const int MaxErrorLength = 400;

    /// <summary>True when the service turned the request away for being busy, or did not answer in time; not when the user stopped it.</summary>
    private static bool IsBusy(Exception e, CancellationToken ct) =>
        e is System.ClientModel.ClientResultException { Status: 429 or 503 } or TimeoutException || (e is OperationCanceledException && !ct.IsCancellationRequested);

    public async Task<IReadOnlyList<ClipNote>> ReviewAsync(Episode episode, CancellationToken ct)
    {
        var script = string.Join("\n\n", episode.Clips.Select((c, i) => $"Clip {i + 1}: {c.Title}\n{c.Active.Dialogue}"));
        var review = await Ask<ReviewDto>(settings.FastChatDeployment, ScriptSchemas.ReviewSystem, Fenced("existing", "", script), "review", ScriptSchemas.Review, ct);
        // One note a clip, and only for clips that are there: the model's numbering is not trusted further than that.
        return [.. review.Notes.Where(n => n is not null && n.Clip >= 1 && n.Clip <= episode.Clips.Count && !string.IsNullOrWhiteSpace(n.Note)).DistinctBy(n => n.Clip)];
    }

    // The editor's note is a model's words, so it is fenced like the clip's own.
    public async Task<string> ReviseAsync(Clip clip, string note, CancellationToken ct) =>
        (await Ask<RewriteDto>(settings.ChatDeployment, ScriptSchemas.ReviseSystem,
            Fenced("existing", " what=\"the clip's title\"", clip.Title) + Fenced("existing", " what=\"the narration\"", clip.Active.Dialogue) + Fenced("existing", " what=\"the editor's note\"", note),
            "rewrite", ScriptSchemas.Rewrite, ct)).Dialogue;

    // The topic is not sent: a new depth may use only what the existing script already says.
    public async Task<TierScript> WriteTierAsync(string topic, Clip clip, Tier tier, CancellationToken ct)
    {
        var request = new StringBuilder()
            .AppendLine($"Depth to write: {tier.ToString().ToLowerInvariant()}")
            .AppendLine(Visuals.Kinds.FirstOrDefault(k => k.Kind == clip.Visual.Kind) is { } kind ? $"Picture type: {kind.Key}. {kind.Brief}" : "Picture type: the user's own footage. visualPrompt: one sentence describing a picture that would suit the clip.").AppendLine()
            // A title and its words were written by a model, from repository passages anyone can edit, so they are fenced like those passages.
            .Append(Fenced("existing", " what=\"the clip's title\"", clip.Title))
            .Append(Fenced("existing", $" depth=\"{clip.ActiveTier.ToString().ToLowerInvariant()}\"", clip.Active.Dialogue));
        var script = await Ask<TierDto>(settings.FastChatDeployment, ScriptSchemas.TierSystem, request.ToString(), "tier", ScriptSchemas.Tier, ct);
        return new TierScript(script.Dialogue, script.VisualPrompt, script.Pose);
    }

    public async Task<bool> CoreChangedAsync(string oldDialogue, string newDialogue, CancellationToken ct) =>
        (await AskLight<DriftDto>(ScriptSchemas.DriftSystem,
            Fenced("existing", " version=\"first\"", oldDialogue) + Fenced("existing", " version=\"second\"", newDialogue), "drift", ScriptSchemas.Drift, ct)).CoreChanged;

    public async Task<string> RewriteToLengthAsync(string dialogue, int targetWords, CancellationToken ct) =>
        (await Ask<RewriteDto>(settings.ChatDeployment, ScriptSchemas.RewriteSystem,
            $"Rewrite the line below to about {targetWords} words.\n" + Fenced("existing", "", dialogue), "rewrite", ScriptSchemas.Rewrite, ct)).Dialogue;

    public async Task<IReadOnlyList<Teaser>> SuggestTeasersAsync(Episode episode, IReadOnlyDictionary<Guid, TimeSpan> lengths, CancellationToken ct)
    {
        // Numbered and timed, so the model can name a moment by clip and second; fenced, as a model wrote it. What a
        // model saw in the owner's films says where in them the moment worth cutting to is.
        var script = string.Join("\n\n", episode.Clips.Select((c, i) => FormattableString.Invariant(
            $"Clip {i + 1} ({c.Beat ?? "no beat"}, {(lengths.TryGetValue(c.Id, out var length) ? length.TotalSeconds : 0):0.0} s): {c.Title}\n{(c.OwnSound ? "[the owner's film, with its own sound] " : "")}{c.Active.Dialogue}{(c.FootageSeen is { } seen ? $"\n[seen in the film] {seen.ReplaceLineEndings(" ")}" : "")}")));
        var reply = await AskLight<TeasersDto>(ScriptSchemas.TeasersSystem, Fenced("existing", " what=\"the finished script\"", script), "teasers", ScriptSchemas.Teasers, ct);
        var suggested = reply.Teasers.Where(t => t is not null).Select(t => new Teaser(t.Hook, [.. t.Moments
            .Where(m => m is not null && m.Clip >= 1 && m.Clip <= episode.Clips.Count)
            .Select(m => new TeaserMoment(episode.Clips[m.Clip - 1].Id, TimeSpan.FromSeconds(Math.Max(0, m.Start)), TimeSpan.FromSeconds(Math.Max(0, m.Length))))]));
        // The model's numbers are not trusted further than the rules allow; the rules' own teasers fill any gap.
        return Teasers.Fit([.. suggested, .. Teasers.ByRule(episode, lengths)], lengths);
    }

    public Task<PublishNotes> WritePublishNotesAsync(Episode episode, CancellationToken ct) =>
        AskLight<PublishNotes>(ScriptSchemas.PublishSystem,
            Fenced("existing", "", string.Join("\n\n", episode.Clips.Select(c => $"{c.Title}\n{c.Active.Dialogue}"))), "publish", ScriptSchemas.Publish, ct);

    /// <summary>The most terms a lesson is searched by, besides its own words, so a chatty reply cannot fan out into dozens of searches.</summary>
    private const int MaxSearchTerms = 8;

    public async Task<IReadOnlyList<string>> SearchTermsAsync(string topic, CancellationToken ct) =>
        [.. (await AskLight<SearchDto>(ScriptSchemas.SearchSystem, Fenced("topic", "", Capped(topic)), "search", ScriptSchemas.Search, ct)).Terms
            .Where(t => t is not null).Select(t => t.Trim()).Where(t => t.Length is >= 2 and <= 40).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxSearchTerms)];

    public async Task<FootageSeen?> WatchAsync(IReadOnlyList<Frame> frames, string note, CancellationToken ct)
    {
        if (See is null || frames.Count == 0) return null;
        var reply = await See(settings.FastChatDeployment, ScriptSchemas.WatchSystem,
            note.Trim().Length > 0 ? Fenced("existing", " what=\"the owner's note on the film\"", note) : "The owner wrote no note on it.",
            [.. frames.Select(f => (FormattableString.Invariant($"At {f.At.TotalSeconds:0.0} s:"), f.Path))], "watch", ScriptSchemas.Watch, ct);
        var seen = Read<FootageSeen>(reply);
        return seen with { Highlight = Math.Clamp(seen.Highlight, 0, frames[^1].At.TotalSeconds + 1) };
    }

    public async Task<SceneVerdict> CheckSceneAsync(string framePath, CancellationToken ct)
    {
        if (See is null) return new SceneVerdict(true, "");
        var verdict = Read<SceneVerdict>(await See(settings.FastChatDeployment, ScriptSchemas.SceneCheckSystem, "The frame:", [("", framePath)], "scenecheck", ScriptSchemas.SceneCheck, ct));
        // A fault with nothing said about it cannot be put right, so it is not sent back.
        return verdict.Fine || string.IsNullOrWhiteSpace(verdict.Problem) ? new SceneVerdict(true, "") : verdict;
    }

    public async Task<string?> CheckHostAsync(string picturePath, string sheetPath, CancellationToken ct)
    {
        if (See is null) return null;
        var verdict = Read<HostDto>(await See(settings.FastChatDeployment, ScriptSchemas.HostCheckSystem, "Compare these two pictures.",
            [("The design sheet:", sheetPath), ("The new picture:", picturePath)], "hostcheck", ScriptSchemas.HostCheck, ct));
        return verdict.Same || string.IsNullOrWhiteSpace(verdict.Difference) ? null : verdict.Difference.Trim();
    }
}
