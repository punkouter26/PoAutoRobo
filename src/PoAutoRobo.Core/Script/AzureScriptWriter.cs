using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace PoAutoRobo.Core.Script;

/// <summary>Sends one request and returns the model's reply, which must be JSON matching <paramref name="schema"/>.</summary>
/// <param name="onText">Given each piece of the reply as it arrives; null when nobody is watching.</param>
public delegate Task<string> JsonChat(string deployment, string system, string user, string schemaName, string schema, Action<string>? onText, CancellationToken ct);

/// <summary>Writes scripts with Azure OpenAI. The <see cref="JsonChat"/> seam lets tests supply replies without a network.</summary>
public sealed partial class AzureScriptWriter(AppSettings settings, JsonChat chat) : IScriptWriter
{
    // A reply with a field missing or null fails to read, so the records below can be trusted as written.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private sealed record TierDto(string Dialogue, string VisualPrompt, string Pose);
    private sealed record ClipDto(string Title, TierDto B, string? Kind = null);
    private sealed record EpisodeDto(string Title, List<ClipDto> Clips);
    private sealed record DriftDto(bool CoreChanged);
    private sealed record RewriteDto(string Dialogue);
    private sealed record ReviewDto(List<ClipNote> Notes);
    private sealed record PlannedClipDto(string Title, string Kind, string About);
    private sealed record OutlineDto(string Title, List<PlannedClipDto> Clips);

    /// <summary>Told, in words fit to show, of anything out of the ordinary in how a script came to be written.</summary>
    public Action<string>? Note { get; set; }

    /// <summary>Told what each request used: its name in the spend log, how many tokens, and their price when the rates are known.</summary>
    public Action<string, int, decimal>? Used { get; set; }

    /// <summary>Which picture types can be made on this computer; the model is offered only those.</summary>
    public Func<VisualKind, bool> CanMake { get; set; } = _ => true;

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
        return writer = new AzureScriptWriter(settings, async (deployment, system, user, schemaName, schema, onText, ct) =>
        {
            var chatOptions = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(schemaName, BinaryData.FromString(schema), jsonSchemaIsStrict: true),
            };
            ChatMessage[] messages = [new SystemChatMessage(system), new UserChatMessage(user)];
            var reply = new StringBuilder();
            try
            {
                await foreach (var update in client.GetChatClient(deployment).CompleteChatStreamingAsync(messages, chatOptions, ct))
                {
                    // A declined or cut-off reply is not valid JSON; say what happened, and what did it, instead of "could not be read".
                    if (!string.IsNullOrEmpty(update.RefusalUpdate))
                        throw new ScriptDeclinedException(DeclinedBy.Model, null, deployment);
                    if (update.FinishReason == ChatFinishReason.ContentFilter)
                        throw new ScriptDeclinedException(DeclinedBy.ReplyFilter, null, deployment);
                    if (update.FinishReason == ChatFinishReason.Length)
                        throw new InvalidOperationException("The script was cut off before it finished. Choose a shorter episode length and try again.");
                    if (update.Usage is { } usage)
                    {
                        var cached = usage.InputTokenDetails?.CachedTokenCount ?? 0;
                        var rates = deployment == settings.FastChatDeployment ? settings.FastChatRates : settings.ChatRates;
                        writer?.Used?.Invoke(SpendLog.ScriptTokens, usage.TotalTokenCount, rates?.Cost(usage.InputTokenCount, cached, usage.OutputTokenCount) ?? 0);
                        if (cached > 0)
                            writer?.Used?.Invoke(SpendLog.CachedScriptTokens, cached, 0); // shows whether the long, unchanging prompt is being reused
                    }
                    foreach (var part in update.ContentUpdate)
                    {
                        reply.Append(part.Text);
                        onText?.Invoke(part.Text);
                    }
                }
            }
            // The filter in front of the model turns a request away with an ordinary "bad request", and says in the
            // reply which kind of content it objected to and how strongly. That is worth telling apart from a fault.
            catch (System.ClientModel.ClientResultException e) when (e.Status == 400 && ScriptDeclinedException.FilterVerdict(e.GetRawResponse()?.Content.ToString() ?? "") is { } verdict)
            {
                throw new ScriptDeclinedException(DeclinedBy.RequestFilter, verdict.Length > 0 ? verdict : null, deployment);
            }
            return reply.ToString();
        });
    }

    public async Task<Episode> WriteEpisodeAsync(string topic, Subject subject, IReadOnlyList<GroundingSnippet> grounding, EpisodeLength length, CancellationToken ct, IProgress<IReadOnlyList<string>>? clipsWritten = null)
    {
        var prompt = new StringBuilder().AppendLine($"Number of clips: {length.InWords}.").AppendLine().Append(Fenced("topic", "", Capped(topic)));
        foreach (var snippet in grounding)
            prompt.AppendLine().Append(Fenced("reference", $" source=\"{Attribute(snippet.Repo)} · {Attribute(snippet.Path)}\" url=\"{Attribute(snippet.Url)}\"", snippet.Text));

        // Every clip ends with its pose, so counting those in the reply so far counts finished clips. The first
        // title in the reply is the episode's own; the ones after it belong to the clips, in order.
        // ponytail: rescans the whole reply on each piece. It is a few kilobytes; keep a running position if it ever shows up.
        var soFar = new StringBuilder();
        var written = 0;
        Action<string>? watch = clipsWritten is null ? null : piece =>
        {
            var text = soFar.Append(piece).ToString();
            var count = PoseKey().Count(text);
            if (count == written) return;
            written = count;
            clipsWritten.Report([.. TitleValue().Matches(text).Skip(1).Take(count).Select(m => JsonSerializer.Deserialize<string>($"\"{m.Groups[1].Value}\"")!)]);
        };

        IReadOnlyList<KindInfo> kinds = [.. Visuals.Kinds.Where(k => CanMake(k.Kind))];
        var (system, schema) = (ScriptSchemas.System(subject, kinds), ScriptSchemas.Episode(kinds));
        EpisodeDto draft;
        try
        {
            draft = await Ask<EpisodeDto>(settings.ChatDeployment, system, prompt.ToString(), "episode", schema, watch, ct);
            // Too many clips are simply trimmed below. Too few is topped up once, asking only for the clips that are missing
            // so the ones already paid for are kept.
            if (draft.Clips.Count < length.MinClips)
            {
                var missing = length.MinClips - draft.Clips.Count;
                prompt.AppendLine().AppendLine(
                    $"Your last answer had only {draft.Clips.Count} clips: {string.Join("; ", draft.Clips.Select(c => c.Title))}. " +
                    $"Write {missing} more on subtopics those do not cover, and return only the new clips.");
                var more = await Ask<EpisodeDto>(settings.ChatDeployment, system, prompt.ToString(), "episode", schema, null, ct);
                draft = draft with { Clips = [.. draft.Clips, .. more.Clips] };
            }
        }
        // A whole script is one reply, so one passage turned away loses all of it. It is then written a clip at a
        // time, and only the clip in question is lost. Not when the topic itself was turned away before the model
        // saw it: every piece would carry the same topic and meet the same answer.
        catch (ScriptDeclinedException declined) when (declined.By != DeclinedBy.RequestFilter)
        {
            Note?.Invoke($"{declined.What} Writing it one clip at a time, so that only the clip in question is lost.");
            draft = await WriteInPiecesAsync(prompt.ToString(), subject, kinds, length, clipsWritten, ct);
        }
        if (draft.Clips.Count < length.MinClips)
            throw new InvalidDataException($"The script came back with only {draft.Clips.Count} clips. Please try again.");

        var clips = draft.Clips.Take(length.MaxClips).Select(c => new Clip(
            Guid.NewGuid(), c.Title, Tier.B,
            new Dictionary<Tier, TierScript> { [Tier.B] = new(c.B.Dialogue, c.B.VisualPrompt, c.B.Pose) },
            new VisualSpec(Visuals.KindFor(c.Kind)), HostVisible: subject != Subject.Essay)).ToList();
        return new Episode(draft.Title, topic, clips, MixSeed: Random.Shared.Next()) { Subject = subject };
    }

    /// <summary>What a clip the model would not write says until the user writes it.</summary>
    public const string LeftToWrite = "Write what the narrator says here. The script service would not write this clip.";

    /// <summary>
    /// The same episode as one plan and then one request a clip. A clip that is turned away is left for the user to
    /// write, under the title and picture the plan gave it, and the rest are written as usual.
    /// </summary>
    /// <param name="request">The request as it was made for the whole script: the number of clips, the topic and the references.</param>
    private async Task<EpisodeDto> WriteInPiecesAsync(string request, Subject subject, IReadOnlyList<KindInfo> kinds, EpisodeLength length, IProgress<IReadOnlyList<string>>? clipsWritten, CancellationToken ct)
    {
        var outline = await Ask<OutlineDto>(settings.ChatDeployment, ScriptSchemas.OutlineSystem(subject, kinds), request, "outline", ScriptSchemas.Outline(kinds), null, ct);
        var planned = outline.Clips.Take(length.MaxClips).ToList();
        var plan = string.Join('\n', planned.Select((c, i) => $"{i + 1}. {c.Title}: {c.About}"));
        var clips = new List<ClipDto>(planned.Count);
        var left = new List<string>();
        for (var i = 0; i < planned.Count; i++)
        {
            var clip = planned[i];
            var mine = new StringBuilder(request).AppendLine()
                .Append(Fenced("existing", " what=\"the plan of the whole video\"", $"{outline.Title}\n{plan}"))
                .AppendLine($"Write clip {i + 1} of {planned.Count}.")
                .AppendLine(kinds.FirstOrDefault(k => k.Key == clip.Kind) is { } kind ? $"Picture type: {kind.Key}. {kind.Brief}" : "Picture type: title. visualPrompt: the title.");
            try
            {
                clips.Add(new ClipDto(clip.Title, await Ask<TierDto>(settings.ChatDeployment, ScriptSchemas.ClipSystem(subject), mine.ToString(), "tier", ScriptSchemas.Tier, null, ct), clip.Kind));
            }
            catch (ScriptDeclinedException)
            {
                // Kept as a title card: there is nothing yet to draw a picture of.
                clips.Add(new ClipDto(clip.Title, new TierDto(LeftToWrite, clip.About, "none"), "title"));
                left.Add(clip.Title);
            }
            clipsWritten?.Report([.. clips.Select(c => c.Title)]);
        }
        if (left.Count == planned.Count)
            throw new ScriptDeclinedException(DeclinedBy.Model, null, settings.ChatDeployment); // nothing was written: there is no episode to hand back
        if (left.Count > 0)
            Note?.Invoke($"{(left.Count == 1 ? "1 clip was" : $"{left.Count} clips were")} left for you to write, as the script service would not: {string.Join(", ", left)}.");
        return new EpisodeDto(outline.Title, clips);
    }

    public async Task<SaferPicture> RethinkPictureAsync(Clip clip, CancellationToken ct) =>
        await Ask<SaferPicture>(settings.FastChatDeployment, ScriptSchemas.RethinkSystem,
            Fenced("existing", " what=\"the picture that was declined\"", clip.Active.VisualPrompt) + Fenced("existing", " what=\"what the narrator says over it\"", clip.Active.Dialogue),
            "rethink", ScriptSchemas.Rethink, null, ct);

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
                .Append(Fenced("existing", " what=\"the script you wrote last time, which did not run\"", failed.Script))
                .Append(Fenced("existing", " what=\"what the browser said went wrong\"", problem is { Length: > MaxErrorLength } ? problem[..MaxErrorLength] : problem ?? ""))
                .AppendLine("Write the whole scene again so that it runs.");

        // Moving words and charts are near to a set pattern, which the fast model draws as well and far more cheaply.
        // A diagram is not; but when the main model is too busy or too slow to answer, the fast one's is better than none.
        var deployment = request.Kind is VisualKind.Chart or VisualKind.KineticText ? settings.FastChatDeployment : settings.ChatDeployment;
        try
        {
            return await Ask<Scene>(deployment, ScriptSchemas.SceneSystem, prompt.ToString(), "scene", ScriptSchemas.Scene, null, ct);
        }
        catch (Exception e) when (deployment != settings.FastChatDeployment && IsBusy(e, ct))
        {
            return await Ask<Scene>(settings.FastChatDeployment, ScriptSchemas.SceneSystem, prompt.ToString(), "scene", ScriptSchemas.Scene, null, ct);
        }
    }

    private const int MaxErrorLength = 400;

    /// <summary>True when the service turned the request away for being busy, or did not answer in time; not when the user stopped it.</summary>
    private static bool IsBusy(Exception e, CancellationToken ct) =>
        e is System.ClientModel.ClientResultException { Status: 429 or 503 } or TimeoutException || (e is OperationCanceledException && !ct.IsCancellationRequested);

    public async Task<IReadOnlyList<ClipNote>> ReviewAsync(Episode episode, CancellationToken ct)
    {
        var script = string.Join("\n\n", episode.Clips.Select((c, i) => $"Clip {i + 1}: {c.Title}\n{c.Active.Dialogue}"));
        var review = await Ask<ReviewDto>(settings.FastChatDeployment, ScriptSchemas.ReviewSystem, Fenced("existing", "", script), "review", ScriptSchemas.Review, null, ct);
        // One note a clip, and only for clips that are there: the model's numbering is not trusted further than that.
        return [.. review.Notes.Where(n => n is not null && n.Clip >= 1 && n.Clip <= episode.Clips.Count && !string.IsNullOrWhiteSpace(n.Note)).DistinctBy(n => n.Clip)];
    }

    // The topic is not sent: a new depth may use only what the existing script already says.
    public async Task<TierScript> WriteTierAsync(string topic, Subject subject, Clip clip, Tier tier, CancellationToken ct)
    {
        var request = new StringBuilder()
            .AppendLine($"Depth to write: {tier.ToString().ToLowerInvariant()}")
            .AppendLine(Visuals.Kinds.FirstOrDefault(k => k.Kind == clip.Visual.Kind) is { } kind ? $"Picture type: {kind.Key}. {kind.Brief}" : "Picture type: the user's own footage. visualPrompt: one sentence describing a picture that would suit the clip.").AppendLine()
            // A title and its words were written by a model from a topic that may have come from a news feed, so they are fenced like the topic.
            .Append(Fenced("existing", " what=\"the clip's title\"", clip.Title))
            .Append(Fenced("existing", $" depth=\"{clip.ActiveTier.ToString().ToLowerInvariant()}\"", clip.Active.Dialogue));
        var script = await Ask<TierDto>(settings.FastChatDeployment, ScriptSchemas.TierSystem(subject), request.ToString(), "tier", ScriptSchemas.Tier, null, ct);
        return new TierScript(script.Dialogue, script.VisualPrompt, script.Pose);
    }

    public async Task<bool> CoreChangedAsync(string oldDialogue, string newDialogue, CancellationToken ct) =>
        (await Ask<DriftDto>(settings.FastChatDeployment, ScriptSchemas.DriftSystem,
            Fenced("existing", " version=\"first\"", oldDialogue) + Fenced("existing", " version=\"second\"", newDialogue), "drift", ScriptSchemas.Drift, null, ct)).CoreChanged;

    public async Task<string> RewriteToLengthAsync(string dialogue, int targetWords, CancellationToken ct) =>
        (await Ask<RewriteDto>(settings.ChatDeployment, ScriptSchemas.RewriteSystem,
            $"Rewrite the line below to about {targetWords} words.\n" + Fenced("existing", "", dialogue), "rewrite", ScriptSchemas.Rewrite, null, ct)).Dialogue;

    public Task<PublishNotes> WritePublishNotesAsync(Episode episode, CancellationToken ct) =>
        Ask<PublishNotes>(settings.FastChatDeployment, ScriptSchemas.PublishSystem,
            Fenced("existing", "", string.Join("\n\n", episode.Clips.Select(c => $"{c.Title}\n{c.Active.Dialogue}"))), "publish", ScriptSchemas.Publish, null, ct);

    private static string Capped(string topic) => topic.Length > IScriptWriter.MaxTopicLength ? topic[..IScriptWriter.MaxTopicLength] : topic;

    // Text from a feed, a repository or a paste goes inside a tag the system prompt tells the model not to obey.
    // Any copy of the closing tag, however it is spaced or cased, is removed first, so the text cannot end its own fence early.
    private static string Fenced(string tag, string attributes, string text) =>
        $"<{tag}{attributes}>\n{Regex.Replace(text, $@"<\s*/\s*{tag}\s*>", "", RegexOptions.IgnoreCase)}\n</{tag}>\n";

    // A repository path or link sits inside quotes on the opening tag; it must not be able to close them.
    private static string Attribute(string value) => value.Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    private async Task<T> Ask<T>(string deployment, string system, string user, string schemaName, string schema, Action<string>? onText, CancellationToken ct)
    {
        var reply = await chat(deployment, system, user, schemaName, schema, onText, ct);
        try
        {
            var value = JsonSerializer.Deserialize<T>(reply, Json);
            // The serializer checks every field, but not for a null in the middle of a list.
            if (value is null || (value is EpisodeDto episode && episode.Clips.Contains(null!)) || (value is OutlineDto outline && outline.Clips.Contains(null!)))
                throw new JsonException("Reply is missing required fields.");
            // Some models answer in exactly the shape asked, with "I cannot assist with that" where the script should
            // be. Taken at its word that would be saved, voiced and drawn as an episode.
            string[] words = value switch
            {
                EpisodeDto script => [script.Title, .. script.Clips.Select(c => c.B.Dialogue)],
                OutlineDto plan => [plan.Title, .. plan.Clips.Select(c => c.About)],
                TierDto tier => [tier.Dialogue],
                RewriteDto rewrite => [rewrite.Dialogue],
                _ => [],
            };
            if (words.Any(ScriptDeclinedException.IsRefusal))
                throw new ScriptDeclinedException(DeclinedBy.Model, null, deployment);
            return value;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The model's reply could not be read. Please try again.", e);
        }
    }

    [GeneratedRegex("\"pose\"")]
    private static partial Regex PoseKey();

    [GeneratedRegex(@"""title""\s*:\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex TitleValue();
}
