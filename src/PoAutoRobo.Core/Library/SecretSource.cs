using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;

namespace PoAutoRobo.Core.Library;

/// <summary>Reads one secret by name.</summary>
/// <returns>The secret's value, or null when there is no secret of that name.</returns>
public delegate Task<string?> SecretSource(string name, CancellationToken ct);

/// <summary>Azure Key Vault, read as the signed-in Azure user. Nothing is written to disk.</summary>
public static class KeyVault
{
    public static readonly Uri DefaultVault = new(AppSettings.Choice("POAUTOROBO_VAULT", "https://kv-poshared.vault.azure.net/"));

    /// <summary>The app's own vault, read as the user signed in with <c>az login</c>.</summary>
    public static SecretSource Default() => Reader(DefaultVault, AppSettings.SignedInUser);

    public static SecretSource Reader(Uri vault, TokenCredential credential)
    {
        var client = new SecretClient(vault, credential);
        return async (name, ct) =>
        {
            try
            {
                return (await client.GetSecretAsync(name, cancellationToken: ct)).Value.Value;
            }
            catch (RequestFailedException e) when (e.Status == 404)
            {
                return null;
            }
        };
    }
}
