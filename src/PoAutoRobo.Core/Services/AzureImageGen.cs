using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoAutoRobo.Core.Services;

/// <summary>Pictures from an Azure OpenAI GPT-image deployment, over plain REST.</summary>
public sealed class AzureImageGen(AppSettings settings, HttpClient http) : IImageGen
{
    private const string ApiVersion = "2025-04-01-preview";

    /// <summary>Tries per picture when the service says it is rate-limited. Other failures are never retried.</summary>
    public const int MaxAttempts = 6;

    // The deployment allows only a few pictures a minute, so a batch always hits the limit; these bound the waiting.
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(60);

    /// <summary>"low", "medium" or "high". Cost and time rise steeply with each step.</summary>
    public string Quality { get; init; } = "medium";

    /// <summary>How waiting is done; replaced in tests so they do not sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public static HttpClient NewHttpClient() => new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task GenerateAsync(ImageRequest request, string outputPath, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var message = await BuildAsync(request, ct); // a request can be sent only once, so each try builds its own
            using var response = await http.SendAsync(message, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt == MaxAttempts)
                    throw new InvalidOperationException("The picture service is busy. Wait a minute and generate again; pictures already made are kept and not charged twice.");
                var asked = response.Headers.RetryAfter?.Delta;
                await Delay(asked is { } wait ? (wait < LongestWait ? wait : LongestWait) : DefaultWait, ct);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"The picture could not be made. {ErrorMessage(body) ?? $"The service answered {(int)response.StatusCode}."}");

            using var json = JsonDocument.Parse(body);
            var bytes = Convert.FromBase64String(json.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString()!);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllBytesAsync(outputPath, bytes, ct);
            return;
        }
    }

    private async Task<HttpRequestMessage> BuildAsync(ImageRequest request, CancellationToken ct)
    {
        // A reference picture goes to the edit endpoint as a file; without one it is an ordinary generation.
        var operation = request.ReferencePath is null ? "generations" : "edits";
        var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(settings.Endpoint!, $"openai/deployments/{settings.ImageDeployment}/images/{operation}?api-version={ApiVersion}"));
        var token = await settings.Credential.GetTokenAsync(new Azure.Core.TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), ct);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        if (request.ReferencePath is null)
        {
            message.Content = JsonContent.Create(new { prompt = request.Prompt, size = request.Size, quality = Quality, n = 1 });
        }
        else
        {
            var image = new ByteArrayContent(await File.ReadAllBytesAsync(request.ReferencePath, ct));
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            message.Content = new MultipartFormDataContent
            {
                { image, "image", "reference.png" },
                { new StringContent(request.Prompt), "prompt" },
                { new StringContent(request.Size), "size" },
                { new StringContent(Quality), "quality" },
                { new StringContent("1"), "n" },
            };
        }
        return message;
    }

    private static string? ErrorMessage(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("error").GetProperty("message").GetString();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
