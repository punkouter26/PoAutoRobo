using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace PoAutoRobo.Core.Services;

public interface ISecretSource
{
    /// <returns>The secret's value, or null when the vault has no secret of that name.</returns>
    Task<string?> GetAsync(string name, CancellationToken ct);
}

/// <summary>Reads secrets from Azure Key Vault as the signed-in Azure user. Nothing is written to disk.</summary>
public sealed class KeyVaultSecretSource(Uri vault) : ISecretSource
{
    public static readonly Uri DefaultVault = new("https://kv-poshared.vault.azure.net/");

    private readonly SecretClient _client = new(vault, new DefaultAzureCredential());

    public async Task<string?> GetAsync(string name, CancellationToken ct)
    {
        try
        {
            return (await _client.GetSecretAsync(name, cancellationToken: ct)).Value.Value;
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }
}

/// <summary>Connection details for the live services. Held in memory only; never saved with an episode.</summary>
/// <param name="Endpoint">The Azure AI services resource. One resource and key serve script, voice, images and video.</param>
public sealed record AppSettings(Uri? Endpoint, string? ApiKey, string? GitHubToken)
{
    public static readonly AppSettings Offline = new(null, null, null);

    public string ChatDeployment { get; init; } = "gpt-5.4";
    public string FastChatDeployment { get; init; } = "gpt-5.4-mini";
    public string Voice { get; init; } = "en-US-DavisNeural";

    /// <summary>Change to "gpt-image-2" once that deployment exists; it holds a character's look better.</summary>
    public string ImageDeployment { get; init; } = "gpt-image-1-mini";

    /// <summary>Why the vault could not be read, in words fit to show the user.</summary>
    public string? LoadError { get; init; }

    public static async Task<AppSettings> LoadAsync(ISecretSource vault, CancellationToken ct)
    {
        try
        {
            // Fetched together: each is a separate round trip made before the window can appear.
            var (endpoint, apiKey, gitHub) = (Get("AzureOpenAI--Endpoint"), Get("AzureOpenAI--ApiKey"), Get("GitHub--PAT"));
            await Task.WhenAll(endpoint, apiKey, gitHub);
            return new AppSettings(
                Uri.TryCreate(await endpoint, UriKind.Absolute, out var uri) ? uri : null, await apiKey, await gitHub);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Not signed in, no network, no permission: run on mocks and say why.
            return Offline with { LoadError = e.Message.Split('\n')[0].Trim() };
        }

        async Task<string?> Get(string name) =>
            await vault.GetAsync(name, ct) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    // Records print every property by default; secrets must never reach a log or an error message.
    public override string ToString() =>
        $"AppSettings {{ Endpoint = {Endpoint}, ApiKey = {Mask(ApiKey)}, GitHubToken = {Mask(GitHubToken)} }}";

    private static string Mask(string? secret) => secret is null ? "(none)" : "(set)";
}
