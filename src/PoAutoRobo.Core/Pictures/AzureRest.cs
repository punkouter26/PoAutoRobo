using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PoAutoRobo.Core.Pictures;

/// <summary>
/// The one way a request reaches the Azure AI resource over plain REST: signed as the user, and tried again when the
/// service says it is busy.
/// </summary>
internal static class AzureRest
{
    /// <summary>Tries per request when the service says it is rate-limited. Other failures are never retried.</summary>
    public const int MaxAttempts = 6;

    private static readonly string[] Scope = ["https://cognitiveservices.azure.com/.default"];

    // A deployment allows only a few pictures a minute, so a batch always hits the limit; these bound the waiting.
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(60);

    /// <param name="content">Makes the body afresh for each try, since a request can be sent only once; null for none.</param>
    /// <param name="busy">What to tell the user when every try was turned away.</param>
    /// <returns>The reply, whatever its status other than "busy". The caller disposes it.</returns>
    /// <exception cref="InvalidOperationException">Still busy after <see cref="MaxAttempts"/> tries.</exception>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, AppSettings settings, HttpMethod method, string path, Func<HttpContent>? content,
        Func<TimeSpan, CancellationToken, Task> delay, string busy, HttpCompletionOption completion, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var message = new HttpRequestMessage(method, new Uri(settings.Endpoint!, path)) { Content = content?.Invoke() };
            var token = await settings.Credential.GetTokenAsync(new Azure.Core.TokenRequestContext(Scope), ct);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            var response = await http.SendAsync(message, completion, ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
                return response;

            var asked = response.Headers.RetryAfter?.Delta;
            response.Dispose();
            if (attempt == MaxAttempts)
                throw new InvalidOperationException(busy);
            await delay(asked is { } wait ? (wait < LongestWait ? wait : LongestWait) : DefaultWait, ct);
        }
    }

    /// <summary>Why a request failed, in the service's own words when it gave any.</summary>
    public static string Failure(string what, HttpResponseMessage response, string body) =>
        $"{what} {ErrorMessage(body) ?? $"The service answered {(int)response.StatusCode}."}";

    public static string? ErrorMessage(string body)
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
