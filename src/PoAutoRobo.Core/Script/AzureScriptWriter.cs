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
    private sealed record ClipDto(string Title, TierDto B);
    private sealed record EpisodeDto(string Title, List<ClipDto> Clips);
    private sealed record DriftDto(bool CoreChanged);
    private sealed record RewriteDto(string Dialogue);

    /// <summary>Told what each request used ("script tokens" and how many), for the episode's running cost.</summary>
    public Action<string, int>? Used { get; set; }

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
            await foreach (var update in client.GetChatClient(deployment).CompleteChatStreamingAsync(messages, chatOptions, ct))
            {
                // A declined or cut-off reply is not valid JSON; say what happened instead of "could not be read".
                if (!string.IsNullOrEmpty(update.RefusalUpdate) || update.FinishReason == ChatFinishReason.ContentFilter)
                    throw new InvalidOperationException("The script service declined this request. Reword the topic and try again.");
                if (update.FinishReason == ChatFinishReason.Length)
                    throw new InvalidOperationException("The script was cut off before it finished. Choose a shorter episode length and try again.");
                if (update.Usage is { } usage)
                    writer?.Used?.Invoke("script tokens", usage.TotalTokenCount);
                foreach (var part in update.ContentUpdate)
                {
                    reply.Append(part.Text);
                    onText?.Invoke(part.Text);
                }
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

        var system = ScriptSchemas.System(subject);
        var draft = await Ask<EpisodeDto>(settings.ChatDeployment, system, prompt.ToString(), "episode", ScriptSchemas.Episode, watch, ct);
        // Too many clips are simply trimmed below. Too few is topped up once, asking only for the clips that are missing
        // so the ones already paid for are kept.
        if (draft.Clips.Count < length.MinClips)
        {
            var missing = length.MinClips - draft.Clips.Count;
            prompt.AppendLine().AppendLine(
                $"Your last answer had only {draft.Clips.Count} clips: {string.Join("; ", draft.Clips.Select(c => c.Title))}. " +
                $"Write {missing} more on subtopics those do not cover, and return only the new clips.");
            var more = await Ask<EpisodeDto>(settings.ChatDeployment, system, prompt.ToString(), "episode", ScriptSchemas.Episode, null, ct);
            draft = draft with { Clips = [.. draft.Clips, .. more.Clips] };
        }
        if (draft.Clips.Count < length.MinClips)
            throw new InvalidDataException($"The script came back with only {draft.Clips.Count} clips. Please try again.");

        var clips = draft.Clips.Take(length.MaxClips).Select(c => new Clip(
            Guid.NewGuid(), c.Title, Tier.B,
            new Dictionary<Tier, TierScript> { [Tier.B] = new(c.B.Dialogue, c.B.VisualPrompt, c.B.Pose) },
            new VisualSpec(VisualKind.TitleCard), HostVisible: true)).ToList();
        return new Episode(draft.Title, topic, clips, MixSeed: Random.Shared.Next()) { Subject = subject };
    }

    // The topic is not sent: a new depth may use only what the existing script already says.
    public async Task<TierScript> WriteTierAsync(string topic, Subject subject, Clip clip, Tier tier, CancellationToken ct)
    {
        var request = new StringBuilder()
            .AppendLine($"Depth to write: {tier.ToString().ToLowerInvariant()}")
            .AppendLine($"Clip title: {clip.Title}").AppendLine()
            .Append(Fenced("existing", $" depth=\"{clip.ActiveTier.ToString().ToLowerInvariant()}\"", clip.Active.Dialogue));
        var script = await Ask<TierDto>(settings.FastChatDeployment, ScriptSchemas.TierSystem(subject), request.ToString(), "tier", ScriptSchemas.Tier, null, ct);
        return new TierScript(script.Dialogue, script.VisualPrompt, script.Pose);
    }

    public async Task<bool> CoreChangedAsync(string oldDialogue, string newDialogue, CancellationToken ct) =>
        (await Ask<DriftDto>(settings.FastChatDeployment, ScriptSchemas.DriftSystem,
            $"First version:\n{oldDialogue}\n\nSecond version:\n{newDialogue}", "drift", ScriptSchemas.Drift, null, ct)).CoreChanged;

    public async Task<string> RewriteToLengthAsync(string dialogue, int targetWords, CancellationToken ct) =>
        (await Ask<RewriteDto>(settings.ChatDeployment, ScriptSchemas.RewriteSystem,
            $"Rewrite this to about {targetWords} words:\n{dialogue}", "rewrite", ScriptSchemas.Rewrite, null, ct)).Dialogue;

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
            if (value is null || (value is EpisodeDto episode && episode.Clips.Contains(null!)))
                throw new JsonException("Reply is missing required fields.");
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
