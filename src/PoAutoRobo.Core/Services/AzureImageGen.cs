using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoAutoRobo.Core.Services;

/// <summary>Pictures from an Azure OpenAI GPT-image deployment, over plain REST.</summary>
public sealed class AzureImageGen(AppSettings settings, HttpClient http) : IImageGen
{
    private const string ApiVersion = "2025-04-01-preview";

    /// <summary>"low", "medium" or "high". Cost and time rise steeply with each step.</summary>
    public string Quality { get; init; } = "medium";

    public static HttpClient NewHttpClient() => new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task GenerateAsync(ImageRequest request, string outputPath, CancellationToken ct)
    {
        // A reference picture goes to the edit endpoint as a file; without one it is an ordinary generation.
        var operation = request.ReferencePath is null ? "generations" : "edits";
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(settings.Endpoint!, $"openai/deployments/{settings.ImageDeployment}/images/{operation}?api-version={ApiVersion}"));
        message.Headers.Add("api-key", settings.ApiKey);

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

        using var response = await http.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The picture could not be made. {ErrorMessage(body) ?? $"The service answered {(int)response.StatusCode}."}");

        using var json = JsonDocument.Parse(body);
        var bytes = Convert.FromBase64String(json.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString()!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllBytesAsync(outputPath, bytes, ct);
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
