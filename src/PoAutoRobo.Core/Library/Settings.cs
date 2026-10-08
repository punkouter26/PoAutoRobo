using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace PoAutoRobo.Core.Library;

public interface ISecretSource
{
    /// <returns>The secret's value, or null when the vault has no secret of that name.</returns>
    Task<string?> GetAsync(string name, CancellationToken ct);
}

/// <summary>Reads secrets from Azure Key Vault as the signed-in Azure user. Nothing is written to disk.</summary>
public sealed class KeyVaultSecretSource(Uri vault, TokenCredential credential) : ISecretSource
{
    public static readonly Uri DefaultVault = new(AppSettings.Choice("POAUTOROBO_VAULT", "https://kv-poshared.vault.azure.net/"));

    private readonly SecretClient _client = new(vault, credential);

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
/// <param name="Endpoint">The Azure AI services resource. One resource serves script, voice, images and video.</param>
public sealed record AppSettings(Uri? Endpoint, string? GitHubToken)
{
    /// <summary>The account signed in with <c>az login</c>. Asking only the CLI is much quicker than trying every sign-in source in turn.</summary>
    public static readonly TokenCredential SignedInUser = new AzureCliCredential();

    public static readonly AppSettings Offline = new(null, null);

    /// <summary>Every request to the AI resource is signed as this user, so the app never holds the resource's key.</summary>
    public TokenCredential Credential { get; init; } = SignedInUser;

    /// <summary>True when script, voice and pictures run for real. They share one resource, so they are live or simulated together.</summary>
    public bool IsLive => Endpoint is not null;

    /// <summary>
    /// The one place a deployment, voice or vault is named. Each can be changed without rebuilding by setting the
    /// environment variable given here.
    /// </summary>
    public static string Choice(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : fallback;

    public string ChatDeployment { get; init; } = Choice("POAUTOROBO_CHAT_MODEL", "gpt-5.4");
    public string FastChatDeployment { get; init; } = Choice("POAUTOROBO_FAST_CHAT_MODEL", "gpt-5.4-mini");
    public string Voice { get; init; } = Choice("POAUTOROBO_VOICE", "en-US-DavisNeural");

    /// <summary>"gpt-image-2", once that deployment exists, holds a character's look better.</summary>
    public string ImageDeployment { get; init; } = Choice("POAUTOROBO_IMAGE_MODEL", "gpt-image-1-mini");

    /// <summary>Why the vault could not be read, in words fit to show the user.</summary>
    public string? LoadError { get; init; }

    public static async Task<AppSettings> LoadAsync(ISecretSource vault, CancellationToken ct)
    {
        try
        {
            // Fetched together: each is a separate round trip made before the window can appear.
            var (endpoint, gitHub) = (Get("AzureOpenAI--Endpoint"), Get("GitHub--PAT"));
            await Task.WhenAll(endpoint, gitHub);
            return new AppSettings(Uri.TryCreate(await endpoint, UriKind.Absolute, out var uri) ? uri : null, await gitHub);
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
        $"AppSettings {{ Endpoint = {Endpoint}, GitHubToken = {(GitHubToken is null ? "(none)" : "(set)")} }}";
}
