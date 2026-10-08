using System.ClientModel;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using PoAutoRobo.Core.Models;

namespace PoAutoRobo.Core.Services;

/// <summary>Sends one request and returns the model's reply, which must be JSON matching <paramref name="schema"/>.</summary>
public delegate Task<string> JsonChat(string deployment, string system, string user, string schemaName, string schema, CancellationToken ct);

/// <summary>Writes scripts with Azure OpenAI. The <see cref="JsonChat"/> seam lets tests supply replies without a network.</summary>
public sealed class AzureScriptWriter(AppSettings settings, JsonChat chat) : IScriptWriter
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record TierDto(string Dialogue, string VisualPrompt, string Pose);
    private sealed record ClipDto(string Title, TierDto A, TierDto B, TierDto C);
    private sealed record EpisodeDto(string Title, List<ClipDto> Clips);
    private sealed record DriftDto(bool CoreChanged);
    private sealed record RewriteDto(string Dialogue);

    public static AzureScriptWriter Create(AppSettings settings)
    {
        // A whole episode is one long reply that takes minutes. The default 100s timeout cut it off and then
        // retried three more times, paying for each attempt, so allow the time and retry at most once.
        var options = new AzureOpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromMinutes(10),
            RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 1),
        };
        var client = new AzureOpenAIClient(settings.Endpoint!, new ApiKeyCredential(settings.ApiKey!), options);
        return new AzureScriptWriter(settings, async (deployment, system, user, schemaName, schema, ct) =>
        {
            var chatOptions = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(schemaName, BinaryData.FromString(schema), jsonSchemaIsStrict: true),
            };
            ChatMessage[] messages = [new SystemChatMessage(system), new UserChatMessage(user)];
            var reply = await client.GetChatClient(deployment).CompleteChatAsync(messages, chatOptions, ct);
            return reply.Value.Content[0].Text;
        });
    }

    public async Task<Episode> WriteEpisodeAsync(string topic, IReadOnlyList<GroundingSnippet> grounding, EpisodeLength length, CancellationToken ct)
    {
        var prompt = new StringBuilder().AppendLine($"Topic: {topic}").AppendLine($"Number of clips: {length.InWords}.");
        if (grounding.Count > 0)
        {
            prompt.AppendLine().AppendLine("Reference snippets from official repositories:");
            foreach (var snippet in grounding)
                prompt.AppendLine().AppendLine($"[{snippet.Repo} · {snippet.Path}]({snippet.Url})").AppendLine(snippet.Text);
        }

        var draft = await Ask<EpisodeDto>(settings.ChatDeployment, ScriptSchemas.System, prompt.ToString(), "episode", ScriptSchemas.Episode, ct);
        if (draft.Clips.Count < length.MinClips || draft.Clips.Count > length.MaxClips)
        {
            prompt.AppendLine().AppendLine($"Your last answer had {draft.Clips.Count} clips. Return {length.InWords}.");
            draft = await Ask<EpisodeDto>(settings.ChatDeployment, ScriptSchemas.System, prompt.ToString(), "episode", ScriptSchemas.Episode, ct);
        }
        if (draft.Clips.Count < length.MinClips)
            throw new InvalidDataException($"The script came back with only {draft.Clips.Count} clips. Please try again.");

        var clips = draft.Clips.Take(length.MaxClips).Select(c => new Clip(
            Guid.NewGuid(), c.Title, Tier.B,
            new Dictionary<Tier, TierScript> { [Tier.A] = Script(c.A), [Tier.B] = Script(c.B), [Tier.C] = Script(c.C) },
            new VisualSpec(VisualKind.TitleCard), HostVisible: true)).ToList();
        return new Episode(draft.Title, topic, clips, MixSeed: Random.Shared.Next());

        static TierScript Script(TierDto t) => new(t.Dialogue, t.VisualPrompt, t.Pose);
    }

    public async Task<bool> CoreChangedAsync(string oldDialogue, string newDialogue, CancellationToken ct) =>
        (await Ask<DriftDto>(settings.FastChatDeployment, ScriptSchemas.DriftSystem,
            $"First version:\n{oldDialogue}\n\nSecond version:\n{newDialogue}", "drift", ScriptSchemas.Drift, ct)).CoreChanged;

    public async Task<string> RewriteToLengthAsync(string dialogue, int targetWords, CancellationToken ct) =>
        (await Ask<RewriteDto>(settings.ChatDeployment, ScriptSchemas.RewriteSystem,
            $"Rewrite this to about {targetWords} words:\n{dialogue}", "rewrite", ScriptSchemas.Rewrite, ct)).Dialogue;

    private async Task<T> Ask<T>(string deployment, string system, string user, string schemaName, string schema, CancellationToken ct)
    {
        var reply = await chat(deployment, system, user, schemaName, schema, ct);
        try
        {
            var value = JsonSerializer.Deserialize<T>(reply, Json);
            // The serializer leaves missing fields null even on non-nullable records, so check the shape by hand.
            if (value is null || value is EpisodeDto { Clips: null } || value is EpisodeDto e && e.Clips.Any(c => c.A is null || c.B is null || c.C is null))
                throw new JsonException("Reply is missing required fields.");
            return value;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The model's reply could not be read. Please try again.", e);
        }
    }
}
