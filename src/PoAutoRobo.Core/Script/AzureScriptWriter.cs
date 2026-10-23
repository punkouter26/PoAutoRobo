using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace PoAutoRobo.Core.Script;

/// <summary>Sends one request and returns the model's reply, which must be JSON matching <paramref name="schema"/>.</summary>
/// <param name="onText">Given each piece of the reply as it arrives; null when nobody is watching.</param>
public delegate Task<string> JsonChat(string deployment, string system, string user, string schemaName, string schema, Action<string>? onText, CancellationToken ct);

/// <summary>The same, with pictures to look at, each after a line of text saying what it is.</summary>
public delegate Task<string> SeeingChat(string deployment, string system, string user, IReadOnlyList<(string Label, string Path)> pictures, string schemaName, string schema, CancellationToken ct);

/// <summary>Writes scripts with Azure OpenAI. The <see cref="JsonChat"/> seam lets tests supply replies without a network.</summary>
public sealed class AzureScriptWriter(AppSettings settings, JsonChat chat) : IScriptWriter
{
    // A reply with a field missing or null fails to read, so the records below can be trusted as written.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private sealed record TierDto(string Dialogue, string VisualPrompt, string Pose);
    // Footage is the number of the owner's file the clip shows, from 1; 0 for none.
    private sealed record ClipDto(string Title, TierDto B, string? Kind = null, string Beat = "", int Footage = 0, bool Host = false);
    private sealed record DriftDto(bool CoreChanged);
    private sealed record RewriteDto(string Dialogue);
    private sealed record ReviewDto(List<ClipNote> Notes);
    private sealed record MomentDto(int Clip, double Start, double Length);
    private sealed record TeaserDto(string Hook, List<MomentDto> Moments);
    private sealed record TeasersDto(List<TeaserDto> Teasers);
    private sealed record PlannedClipDto(string Title, string Kind, string About, string Beat = "", int Footage = 0, bool Host = false);
    private sealed record OutlineDto(string Title, List<PlannedClipDto> Clips);
    private sealed record SearchDto(List<string> Terms);
    private sealed record HostDto(bool Same, string Difference);

    /// <summary>How many clips are written at once. More than this and the service starts turning requests away as too many.</summary>
    private const int ClipsAtOnce = 4;

    /// <summary>Told, in words fit to show, of anything out of the ordinary in how a script came to be written.</summary>
    public Action<string>? Note { get; set; }

    /// <summary>Told what each request used: its name in the spend log, how many tokens, and their price when the rates are known.</summary>
    public Action<string, int, decimal>? Used { get; set; }

    /// <summary>Which picture types can be made on this computer; the model is offered only those.</summary>
    public Func<VisualKind, bool> CanMake { get; set; } = _ => true;

    /// <summary>The fast model with pictures to look at; null when nothing here can see them, and looking is then skipped.</summary>
    public SeeingChat? See { get; init; }

    /// <summary>
    /// A model on this computer (<see cref="AppSettings.LocalModel"/>), for the small jobs that need no great skill. Free;
    /// when it is not running or answers badly, the fast model does the job instead.
    /// </summary>
    public JsonChat? Local { get; init; }

    public static AzureScriptWriter Create(AppSettings settings)
    {
        // A whole episode is one long reply. The default 100s timeout cut it off and then retried three more
        // times, paying for each attempt, so allow the time and retry at most once.
        var options = new AzureOpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromMinutes(10),
            RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 1),
        };
        var client = new AzureOpenAIClient(settings.Endpoint!, settings.Credential, options);
        AzureScriptWriter? writer = null;

        void Count(string deployment, ChatTokenUsage usage)
        {
            var cached = usage.InputTokenDetails?.CachedTokenCount ?? 0;
            var rates = deployment == settings.FastChatDeployment ? settings.FastChatRates : settings.ChatRates;
            writer?.Used?.Invoke(SpendLog.ScriptTokens, usage.TotalTokenCount, rates?.Cost(usage.InputTokenCount, cached, usage.OutputTokenCount) ?? 0);
            if (cached > 0)
                writer?.Used?.Invoke(SpendLog.CachedScriptTokens, cached, 0); // shows whether the long, unchanging prompt is being reused
        }

        ChatCompletionOptions Shape(string schemaName, string schema) => new()
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(schemaName, BinaryData.FromString(schema), jsonSchemaIsStrict: true),
        };

        // The filter in front of the model turns a request away with an ordinary "bad request", and says in the
        // reply which kind of content it objected to and how strongly. That is worth telling apart from a fault.
        async Task<string> Filtered(string deployment, Func<Task<string>> ask)
        {
            try
            {
                return await ask();
            }
            catch (System.ClientModel.ClientResultException e) when (e.Status == 400 && ScriptDeclinedException.FilterVerdict(e.GetRawResponse()?.Content.ToString() ?? "") is { } verdict)
            {
                throw new ScriptDeclinedException(DeclinedBy.RequestFilter, verdict.Length > 0 ? verdict : null, deployment);
            }
        }

        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        return writer = new AzureScriptWriter(settings, (deployment, system, user, schemaName, schema, onText, ct) => Filtered(deployment, async () =>
        {
            ChatMessage[] messages = [new SystemChatMessage(system), new UserChatMessage(user)];
            var reply = new StringBuilder();
            await foreach (var update in client.GetChatClient(deployment).CompleteChatStreamingAsync(messages, Shape(schemaName, schema), ct))
            {
                // A declined or cut-off reply is not valid JSON; say what happened, and what did it, instead of "could not be read".
                if (!string.IsNullOrEmpty(update.RefusalUpdate))
                    throw new ScriptDeclinedException(DeclinedBy.Model, null, deployment);
                if (update.FinishReason == ChatFinishReason.ContentFilter)
                    throw new ScriptDeclinedException(DeclinedBy.ReplyFilter, null, deployment);
                if (update.FinishReason == ChatFinishReason.Length)
                    throw new InvalidOperationException("The script was cut off before it finished. Choose a shorter episode length and try again.");
                if (update.Usage is { } usage)
                    Count(deployment, usage);
                foreach (var part in update.ContentUpdate)
                {
                    reply.Append(part.Text);
                    onText?.Invoke(part.Text);
                }
            }
            return reply.ToString();
        }))
        {
            See = (deployment, system, user, pictures, schemaName, schema, ct) => Filtered(deployment, async () =>
            {
                List<ChatMessageContentPart> parts = [ChatMessageContentPart.CreateTextPart(user)];
                foreach (var (label, path) in pictures)
                {
                    parts.Add(ChatMessageContentPart.CreateTextPart(label));
                    // Low detail is a fixed, small number of tokens a picture, and enough to see what happens in it.
                    var type = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                    parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(await File.ReadAllBytesAsync(path, ct)), type, ChatImageDetailLevel.Low));
                }
                var completion = (await client.GetChatClient(deployment).CompleteChatAsync([new SystemChatMessage(system), new UserChatMessage(parts)], Shape(schemaName, schema), ct)).Value;
                if (completion.Usage is { } usage)
                    Count(deployment, usage);
                if (!string.IsNullOrEmpty(completion.Refusal))
                    throw new ScriptDeclinedException(DeclinedBy.Model, null, deployment);
                if (completion.FinishReason == ChatFinishReason.ContentFilter)
                    throw new ScriptDeclinedException(DeclinedBy.ReplyFilter, null, deployment);
                return completion.Content.Count > 0 ? completion.Content[0].Text : "";
            }),
            Local = settings.LocalModel is null ? null : LocalChat.Create(settings.LocalUrl, http),
        };
    }

    public async Task<Episode> WriteEpisodeAsync(Lesson lesson, IReadOnlyList<GroundingSnippet> grounding, EpisodeLength length, CancellationToken ct, IProgress<IReadOnlyList<string>>? clipsWritten = null)
    {
        var beats = BeatPlan.Beats(lesson.Structure);
        var request = Request(lesson, grounding, length, beats);
        IReadOnlyList<KindInfo> kinds = [.. Visuals.Kinds.Where(k => CanMake(k.Kind))];
        var (system, schema) = (ScriptSchemas.OutlineSystem(kinds), ScriptSchemas.Outline(kinds, beats));

        // A plan is short, so it comes back quickly. Too many clips are simply trimmed below. Too few is topped up
        // once, asking only for the clips that are missing so the ones already planned are kept.
        var outline = await Ask<OutlineDto>(settings.ChatDeployment, system, request, "outline", schema, ct);
        if (outline.Clips.Count < length.MinClips)
        {
            var missing = length.MinClips - outline.Clips.Count;
            var again = request + Environment.NewLine +
                $"Your last plan had only {outline.Clips.Count} clips: {string.Join("; ", outline.Clips.Select(c => c.Title))}. " +
                $"Plan {missing} more on subtopics those do not cover, and return only the new clips." + Environment.NewLine;
            var more = await Ask<OutlineDto>(settings.ChatDeployment, system, again, "outline", schema, ct);
            outline = outline with { Clips = [.. outline.Clips, .. more.Clips] };
        }
        if (outline.Clips.Count < length.MinClips)
            throw new InvalidDataException($"The script came back with only {outline.Clips.Count} clips. Please try again.");

        var (title, written) = await WriteClipsAsync(request, outline, kinds, lesson.Footage, length, clipsWritten, ct);
        var footage = lesson.Footage;
        var clips = written.Select(c =>
        {
            var clip = new Clip(
                Guid.NewGuid(), c.Title, Tier.B,
                new Dictionary<Tier, TierScript> { [Tier.B] = new(c.B.Dialogue, c.B.VisualPrompt, c.B.Pose) },
                new VisualSpec(Visuals.KindFor(c.Kind)), HostVisible: c.Host)
            { Beat = c.Beat };
            return c.Footage >= 1 && c.Footage <= footage.Count ? BeatPlan.WithFootage(clip, footage[c.Footage - 1]) : clip;
        }).ToList();
        // The model's beats and footage numbers are not trusted further than this: the plan is held to its structure.
        var episode = new Episode(title, lesson.Topic, clips, MixSeed: Random.Shared.Next()) { Structure = lesson.Structure };
        return BeatPlan.Repair(episode, footage, length.MaxClips);
    }

    /// <summary>
    /// What every request for this script starts with: the number of clips, the beats, the owner's footage, the
    /// lesson, the series so far and the references. The same text opens every request, so the service has it
    /// cached after the first and charges less for it.
    /// </summary>
    private static string Request(Lesson lesson, IReadOnlyList<GroundingSnippet> grounding, EpisodeLength length, IReadOnlyList<Beat> beats)
    {
        var prompt = new StringBuilder()
            .AppendLine($"Number of clips: {length.InWords}, besides any more the footage needs.")
            .AppendLine($"Beats, in order: {string.Join(", ", beats.Select(b => b.Name))}. Real footage belongs in {string.Join(", ", beats.Where(b => b.ForFootage).Select(b => b.Name))}.")
            .AppendLine(lesson.Footage.Count == 0 ? "Footage: none. Every clip has footage 0." : "Footage:");
        // The owner's own notes, so not fenced: they say where each of their clips belongs. What a model saw in a
        // film, and what is said in it, did not come from the owner, so those are.
        foreach (var (note, i) in lesson.Footage.Select((n, i) => (n, i)))
            prompt.AppendLine(FormattableString.Invariant($"{i + 1}. {note.Length.TotalSeconds:0} seconds, {(note.OwnSound ? "keeps its own sound" : $"narrated over in about {Durations.WordsFor(note.Length)} words")}: {note.Note.ReplaceLineEndings(" ")}"));
        foreach (var (note, i) in lesson.Footage.Select((n, i) => (n, i)))
        {
            if (note.Seen is { } seen)
                prompt.Append(Fenced("existing", $" what=\"what a model saw happen in footage {i + 1}\"", seen.Timeline));
            if (note.Heard is { Count: > 0 } heard)
                prompt.Append(Fenced("existing", $" what=\"what is said in footage {i + 1}\"", string.Join(' ', heard.Select(w => w.Text))));
        }
        if (lesson.Earlier.Count > 0)
            prompt.AppendLine().Append(Fenced("existing", " what=\"earlier episodes of the series\"", string.Join('\n', lesson.Earlier)));
        prompt.AppendLine().Append(Fenced("topic", "", Capped(lesson.Topic)));
        foreach (var snippet in grounding)
            prompt.AppendLine().Append(Fenced("reference", $" source=\"{Attribute(snippet.Repo)} · {Attribute(snippet.Path)}\" url=\"{Attribute(snippet.Url)}\"", snippet.Text));
        return prompt.ToString();
    }

    /// <summary>What a clip the model would not write says until the user writes it.</summary>
    public const string LeftToWrite = "Write what the narrator says here. The script service would not write this clip.";

    /// <summary>
    /// Writes every clip of a plan, a few at a time. A clip that is turned away is left for the user to write, under
    /// the title and picture the plan gave it, and the rest are written as usual. A film that keeps its own sound has
    /// nothing narrated over it, so nothing is asked for it.
    /// </summary>
    /// <param name="request">The request the plan was made from: the number of clips, the lesson and the references.</param>
    private async Task<(string Title, IReadOnlyList<ClipDto> Clips)> WriteClipsAsync(
        string request, OutlineDto outline, IReadOnlyList<KindInfo> kinds, IReadOnlyList<FootageNote> footage, EpisodeLength length, IProgress<IReadOnlyList<string>>? clipsWritten, CancellationToken ct)
    {
        // Each clip is a request of its own, so only as many are written as the length allows. Clips that show the
        // owner's footage are always written; the others fill the room left.
        var room = length.MaxClips - outline.Clips.Count(c => c.Footage > 0);
        var planned = new List<PlannedClipDto>();
        foreach (var clip in outline.Clips)
            if (clip.Footage > 0 || room-- > 0) planned.Add(clip);
        var plan = string.Join('\n', planned.Select((c, i) => $"{i + 1}. {c.Beat}: {c.Title}: {c.About}"));
        var clips = new ClipDto?[planned.Count];
        var left = new bool[planned.Count];
        var asked = 0;

        async Task WriteAsync(int i)
        {
            var clip = planned[i];
            var film = clip.Footage >= 1 && clip.Footage <= footage.Count ? footage[clip.Footage - 1] : null;
            if (film is { OwnSound: true })
            {
                clips[i] = new ClipDto(clip.Title, new TierDto("", clip.About, "none"), clip.Kind, clip.Beat, clip.Footage, clip.Host);
            }
            else
            {
                var mine = new StringBuilder(request).AppendLine()
                    .Append(Fenced("existing", " what=\"the plan of the whole video\"", $"{outline.Title}\n{plan}"))
                    .AppendLine($"Write clip {i + 1} of {planned.Count}.{(film is null ? "" : $" It shows footage {clip.Footage}, in about {Durations.WordsFor(film.Length)} words.")}")
                    .AppendLine(kinds.FirstOrDefault(k => k.Key == clip.Kind) is { } kind ? $"Picture type: {kind.Key}. {kind.Brief}" : "Picture type: title. visualPrompt: the title.");
                Interlocked.Increment(ref asked);
                try
                {
                    clips[i] = new ClipDto(clip.Title, await Ask<TierDto>(settings.ChatDeployment, ScriptSchemas.ClipSystem, mine.ToString(), "tier", ScriptSchemas.Tier, ct), clip.Kind, clip.Beat, clip.Footage, clip.Host);
                }
                catch (ScriptDeclinedException)
                {
                    // Kept as a title card: there is nothing yet to draw a picture of.
                    clips[i] = new ClipDto(clip.Title, new TierDto(LeftToWrite, clip.About, "none"), "title", clip.Beat, clip.Footage, clip.Host);
                    left[i] = true;
                }
            }
            lock (clips)
                clipsWritten?.Report([.. clips.OfType<ClipDto>().Select(c => c.Title)]);
        }

        // The first clip is written alone: the others then find the long opening they share already cached, which
        // costs less and answers sooner. Then the rest, a few at a time.
        if (planned.Count > 0)
            await WriteAsync(0);
        await Batch.EachAsync(Enumerable.Range(1, Math.Max(0, planned.Count - 1)), ClipsAtOnce, WriteAsync, ct);

        var unwritten = planned.Where((_, i) => left[i]).Select(c => c.Title).ToList();
        if (unwritten.Count > 0 && unwritten.Count == asked)
            throw new ScriptDeclinedException(DeclinedBy.Model, null, settings.ChatDeployment); // nothing was written: there is no episode to hand back
        if (unwritten.Count > 0)
            Note?.Invoke($"{(unwritten.Count == 1 ? "1 clip was" : $"{unwritten.Count} clips were")} left for you to write, as the script service would not: {string.Join(", ", unwritten)}.");
        return (outline.Title, [.. clips.OfType<ClipDto>()]);
    }

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

    private static string Capped(string topic) => topic.Length > IScriptWriter.MaxTopicLength ? topic[..IScriptWriter.MaxTopicLength] : topic;

    /// <summary>
    /// Text from a repository, a model or a paste goes inside a tag the system prompts tell the model not to obey.
    /// Any copy of the closing tag, however it is spaced or cased, is removed first, so the text cannot end its own fence early.
    /// </summary>
    public static string Fenced(string tag, string attributes, string text) =>
        $"<{tag}{attributes}>\n{Regex.Replace(text, $@"<\s*/\s*{tag}\s*>", "", RegexOptions.IgnoreCase)}\n</{tag}>\n";

    // A repository path or link sits inside quotes on the opening tag; it must not be able to close them.
    private static string Attribute(string value) => value.Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    private Task<T> Ask<T>(string deployment, string system, string user, string schemaName, string schema, CancellationToken ct) =>
        AskWith<T>(chat, deployment, system, user, schemaName, schema, ct);

    /// <summary>A small job: the model on this computer when there is one, else the fast model.</summary>
    private async Task<T> AskLight<T>(string system, string user, string schemaName, string schema, CancellationToken ct)
    {
        if (Local is { } local && settings.LocalModel is { } model)
            try
            {
                return await AskWith<T>(local, model, system, user, schemaName, schema, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not ScriptDeclinedException)
            {
                // Not running, too slow, or its answer did not read: the fast model is asked instead. Nothing is lost
                // but a moment, so nothing is said.
            }
        return await Ask<T>(settings.FastChatDeployment, system, user, schemaName, schema, ct);
    }

    private static async Task<T> AskWith<T>(JsonChat via, string deployment, string system, string user, string schemaName, string schema, CancellationToken ct)
    {
        var reply = await via(deployment, system, user, schemaName, schema, null, ct);
        var value = Read<T>(reply);
        // Some models answer in exactly the shape asked, with "I cannot assist with that" where the script should
        // be. Taken at its word that would be saved, voiced and drawn as an episode.
        string[] words = value switch
        {
            OutlineDto plan => [plan.Title, .. plan.Clips.Select(c => c.About)],
            TierDto tier => [tier.Dialogue],
            RewriteDto rewrite => [rewrite.Dialogue],
            _ => [],
        };
        if (words.Any(ScriptDeclinedException.IsRefusal))
            throw new ScriptDeclinedException(DeclinedBy.Model, null, deployment);
        return value;
    }

    private static T Read<T>(string reply)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(reply, Json);
            // The serializer checks every field, but not for a null in the middle of a list.
            if (value is null || (value is OutlineDto outline && outline.Clips.Contains(null!)))
                throw new JsonException("Reply is missing required fields.");
            return value;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The model's reply could not be read. Please try again.", e);
        }
    }
}
