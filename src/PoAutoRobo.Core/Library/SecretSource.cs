using Azure;
using Azure.Core;
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
