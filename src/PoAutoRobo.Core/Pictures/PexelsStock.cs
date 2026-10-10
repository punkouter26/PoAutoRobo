using System.Text.Json;

namespace PoAutoRobo.Core.Pictures;

/// <summary>Photographs from the Pexels stock library, which are free to use without credit.</summary>
public sealed class PexelsStock(string apiKey, HttpClient http)
{
    /// <param name="take">0 for the best match; each later take is the next result down, for another photograph of the same thing.</param>
    public async Task FindAsync(string words, int take, string outputPath, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.pexels.com/v1/search?per_page=1&page={take + 1}&orientation=landscape&query={Uri.EscapeDataString(words)}");
        request.Headers.TryAddWithoutValidation("Authorization", apiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The stock photo library answered {(int)response.StatusCode}. Check the Pexels key.");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var photos = json.RootElement.GetProperty("photos");
        if (photos.GetArrayLength() == 0)
            throw new InvalidOperationException(take == 0
                ? $"No stock photo matches “{words}”. Describe the picture in two to four plain words."
                : $"There are no more stock photos of “{words}”.");

        // The address comes from a reply; it is only followed back to the library's own servers.
        var photo = new Uri(photos[0].GetProperty("src").GetProperty("large2x").GetString()!);
        if (photo.Scheme != Uri.UriSchemeHttps || !photo.Host.EndsWith(".pexels.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The stock photo library gave an address that is not its own.");
        await File.WriteAllBytesAsync(outputPath, await http.GetByteArrayAsync(photo, ct), ct);
    }
}
