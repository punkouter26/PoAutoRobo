using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using PoAutoRobo.Core.Models;

namespace PoAutoRobo.Core.Services;

/// <summary>Sends one request and returns the model's reply, which must be JSON matching <paramref name="schema"/>.</summary>
/// <param name="onText">Given each piece of the reply as it arrives; null when nobody is watching.</param>
public delegate Task<string> JsonChat(string deployment, string system, string user, string schemaName, string schema, Action<string>? onText, CancellationToken ct);

/// <summary>Writes scripts with Azure OpenAI. The <see cref="JsonChat"/> seam lets tests supply replies without a network.</summary>
public sealed partial class AzureScriptWriter(AppSettings settings, JsonChat chat) : IScriptWriter
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record TierDto(string Dialogue, string VisualPrompt, string Pose);
    private sealed record ClipDto(string Title, TierDto B);
    private sealed record EpisodeDto(string Title, List<ClipDto> Clips);
    private sealed record DriftDto(bool CoreChanged);
    private sealed record RewriteDto(string Dialogue);

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
        return new AzureScriptWriter(settings, async (deployment, system, user, schemaName, schema, onText, ct) =>
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
                foreach (var part in update.ContentUpdate)
                {
                    reply.Append(part.Text);
                    onText?.Invoke(part.Text);
                }
            }
            return reply.ToString();
        });
    }

    public async Task<Episode> WriteEpisodeAsync(string topic, IReadOnlyList<GroundingSnippet> grounding, EpisodeLength length, CancellationToken ct, IProgress<int>? clipsWritten = null)
    {
        var prompt = new StringBuilder().AppendLine($"Number of clips: {length.InWords}.").AppendLine().Append(Fenced("topic", "", Capped(topic)));
        foreach (var snippet in grounding)
            prompt.AppendLine().Append(Fenced("reference", $" source=\"{snippet.Repo} · {snippet.Path}\" url=\"{snippet.Url}\"", snippet.Text));

        // Every clip ends with its pose, so counting those in the reply so far counts finished clips.
        // ponytail: recounts the whole reply on each piece. It is a few kilobytes; keep a running count if it ever shows up.
        var soFar = new StringBuilder();
        var written = 0;
        Action<string>? watch = clipsWritten is null ? null : piece =>
        {
            var count = PoseKey().Count(soFar.Append(piece).ToString());
            if (count != written)
                clipsWritten.Report(written = count);
        };

        var draft = await Ask<EpisodeDto>(settings.ChatDeployment, ScriptSchemas.System, prompt.ToString(), "episode", ScriptSchemas.Episode, watch, ct);
        // Too many clips are simply trimmed below. Only too few is worth paying for a second script.
        if (draft.Clips.Count < length.MinClips)
        {
            prompt.AppendLine().AppendLine($"Your last answer had {draft.Clips.Count} clips. Return {length.InWords}.");
            soFar.Clear();
            draft = await Ask<EpisodeDto>(settings.ChatDeployment, ScriptSchemas.System, prompt.ToString(), "episode", ScriptSchemas.Episode, watch, ct);
        }
        if (draft.Clips.Count < length.MinClips)
            throw new InvalidDataException($"The script came back with only {draft.Clips.Count} clips. Please try again.");

        var clips = draft.Clips.Take(length.MaxClips).Select(c => new Clip(
            Guid.NewGuid(), c.Title, Tier.B,
            new Dictionary<Tier, TierScript> { [Tier.B] = new(c.B.Dialogue, c.B.VisualPrompt, c.B.Pose) },
            new VisualSpec(VisualKind.TitleCard), HostVisible: true)).ToList();
        return new Episode(draft.Title, topic, clips, MixSeed: Random.Shared.Next());
    }

    public async Task<TierScript> WriteTierAsync(string topic, Clip clip, Tier tier, CancellationToken ct)
    {
        var request = new StringBuilder()
            .AppendLine($"Depth to write: {tier.ToString().ToLowerInvariant()}")
            .AppendLine($"Clip title: {clip.Title}").AppendLine()
            .Append(Fenced("topic", "", Capped(topic))).AppendLine()
            .Append(Fenced("existing", $" depth=\"{clip.ActiveTier.ToString().ToLowerInvariant()}\"", clip.Active.Dialogue));
        var script = await Ask<TierDto>(settings.FastChatDeployment, ScriptSchemas.TierSystem, request.ToString(), "tier", ScriptSchemas.Tier, null, ct);
        return new TierScript(script.Dialogue, script.VisualPrompt, script.Pose);
    }

    public async Task<bool> CoreChangedAsync(string oldDialogue, string newDialogue, CancellationToken ct) =>
        (await Ask<DriftDto>(settings.FastChatDeployment, ScriptSchemas.DriftSystem,
            $"First version:\n{oldDialogue}\n\nSecond version:\n{newDialogue}", "drift", ScriptSchemas.Drift, null, ct)).CoreChanged;

    public async Task<string> RewriteToLengthAsync(string dialogue, int targetWords, CancellationToken ct) =>
        (await Ask<RewriteDto>(settings.ChatDeployment, ScriptSchemas.RewriteSystem,
            $"Rewrite this to about {targetWords} words:\n{dialogue}", "rewrite", ScriptSchemas.Rewrite, null, ct)).Dialogue;

    private static string Capped(string topic) => topic.Length > IScriptWriter.MaxTopicLength ? topic[..IScriptWriter.MaxTopicLength] : topic;

    // Text from a feed, a repository or a paste goes inside a tag the system prompt tells the model not to obey.
    // Any copy of the closing tag is removed first, so the text cannot end its own fence early.
    private static string Fenced(string tag, string attributes, string text) =>
        $"<{tag}{attributes}>\n{text.Replace($"</{tag}>", "", StringComparison.OrdinalIgnoreCase)}\n</{tag}>\n";

    private async Task<T> Ask<T>(string deployment, string system, string user, string schemaName, string schema, Action<string>? onText, CancellationToken ct)
    {
        var reply = await chat(deployment, system, user, schemaName, schema, onText, ct);
        try
        {
            // The serializer leaves missing fields null even on non-nullable records, so check the shape by hand.
            static bool Whole(TierDto? t) => t is { Dialogue: not null, VisualPrompt: not null, Pose: not null };
            var value = JsonSerializer.Deserialize<T>(reply, Json);
            var sound = value switch
            {
                null => false,
                EpisodeDto e => e is { Title: not null, Clips: not null } && e.Clips.All(c => c is { Title: not null } && Whole(c.B)),
                TierDto t => Whole(t),
                RewriteDto r => r.Dialogue is not null,
                _ => true,
            };
            return sound ? value! : throw new JsonException("Reply is missing required fields.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The model's reply could not be read. Please try again.", e);
        }
    }

    [GeneratedRegex("\"pose\"")]
    private static partial Regex PoseKey();
}
