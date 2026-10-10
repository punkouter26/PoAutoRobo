using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoAutoRobo.Core.Pictures;

/// <summary>Footage from an Azure OpenAI video deployment (Sora 2), over plain REST: ask, wait, then download.</summary>
/// <param name="jobsFolder">
/// Where the name of each video still being made is kept. A video is paid for once it is asked for, so one that was
/// stopped or timed out is picked up again the next time the same footage is wanted, not asked (and paid) for twice.
/// </param>
public sealed class AzureVideoGen(AppSettings settings, HttpClient http, string jobsFolder)
{
    /// <summary>Length of every video made. A clip's narration runs longer, so the footage repeats under it.</summary>
    public const int Seconds = 8;

    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);
    private const int MaxPolls = 90; // a quarter of an hour; a video normally takes one to five minutes

    /// <summary>How waiting is done; replaced in tests so they do not sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public async Task GenerateAsync(string prompt, string outputPath, CancellationToken ct)
    {
        var note = Path.Combine(jobsFolder, Files.TextHash($"{settings.VideoDeployment}|{prompt}")[..32] + ".job");
        // A job from an earlier try that the service no longer knows is simply asked for again.
        var begun = File.Exists(note) ? await JsonAsync(HttpMethod.Get, $"/{(await File.ReadAllTextAsync(note, ct)).Trim()}", null, missingIsNull: true, ct) : null;
        var job = begun ?? await BeginAsync(prompt, note, ct);
        var id = Id(job);
        for (var polls = 0; Status(job) is "queued" or "in_progress"; polls++)
        {
            if (polls == MaxPolls)
                throw new InvalidOperationException("The video is taking too long to make. Generate it again later: it carries on being made meanwhile and is not charged twice.");
            await Delay(Poll, ct);
            job = (await JsonAsync(HttpMethod.Get, $"/{id}", null, false, ct))!.Value;
        }
        if (Status(job) != "completed")
        {
            File.Delete(note); // a failed job is over; the next try starts a new one
            throw new InvalidOperationException($"The video could not be made. {(job.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? error.GetProperty("message").GetString() : $"The service said: {Status(job)}.")}");
        }

        using var response = await SendAsync(HttpMethod.Get, $"/{id}/content", null, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The video was made but could not be fetched. The service answered {(int)response.StatusCode}.");
        Files.EnsureFolderFor(outputPath);
        await using (var file = File.Create(outputPath))
            await response.Content.CopyToAsync(file, ct);
        File.Delete(note);
    }

    private async Task<JsonElement> BeginAsync(string prompt, string note, CancellationToken ct)
    {
        var job = (await JsonAsync(HttpMethod.Post, "", () => JsonContent.Create(new { model = settings.VideoDeployment, prompt, size = "1280x720", seconds = $"{Seconds}" }), false, ct))!.Value;
        Directory.CreateDirectory(jobsFolder);
        await File.WriteAllTextAsync(note, Id(job), CancellationToken.None); // it is paid for now, whatever happens next
        return job;
    }

    private static string Id(JsonElement job) => job.GetProperty("id").GetString()!;

    private static string? Status(JsonElement job) => job.GetProperty("status").GetString();

    private async Task<JsonElement?> JsonAsync(HttpMethod method, string path, Func<HttpContent>? content, bool missingIsNull, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (missingIsNull && response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(AzureRest.Failure("The video could not be made.", response, body));
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Func<HttpContent>? content, CancellationToken ct) =>
        AzureRest.SendAsync(http, settings, method, $"openai/v1/videos{path}", content, Delay,
            "The video service is busy. Wait a minute and generate again.", HttpCompletionOption.ResponseHeadersRead, ct);
}
