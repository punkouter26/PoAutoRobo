
namespace PoAutoRobo.Core.Tests;

public sealed class SettingsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly Dictionary<string, string> FullVault = new()
    {
        ["AzureOpenAI--Endpoint"] = "https://example.cognitiveservices.azure.com/",
        ["GitHub--PAT"] = "github-secret-value",
    };

    private sealed class FakeVault(Dictionary<string, string> secrets, Exception? failure = null) : ISecretSource
    {
        public List<string> Asked { get; } = [];

        public Task<string?> GetAsync(string name, CancellationToken ct)
        {
            Asked.Add(name);
            return failure is not null ? Task.FromException<string?>(failure) : Task.FromResult(secrets.GetValueOrDefault(name));
        }
    }

    [Fact]
    public async Task Vault_secrets_map_onto_settings_and_with_an_endpoint_the_services_are_live()
    {
        var vault = new FakeVault(FullVault);

        var settings = await AppSettings.LoadAsync(vault, Ct);

        Assert.Equal(new Uri("https://example.cognitiveservices.azure.com/"), settings.Endpoint);
        Assert.Equal("github-secret-value", settings.GitHubToken);
        Assert.Null(settings.LoadError);
        Assert.True(settings.IsLive);
        // Requests are signed as the user, so the resource's key is never fetched or held.
        Assert.Equal(["AzureOpenAI--Endpoint", "GitHub--PAT"], vault.Asked.Order());
    }

    [Fact]
    public async Task Without_a_usable_endpoint_the_services_are_simulated_and_an_unreachable_vault_also_keeps_the_reason()
    {
        foreach (var endpoint in new[] { "  ", "not a url" })
        {
            var vault = new Dictionary<string, string>(FullVault) { ["AzureOpenAI--Endpoint"] = endpoint };

            var settings = await AppSettings.LoadAsync(new FakeVault(vault), Ct);

            Assert.False(settings.IsLive);
            Assert.Null(settings.LoadError);
            Assert.Equal("github-secret-value", settings.GitHubToken); // grounding does not depend on the AI resource
        }

        var offline = await AppSettings.LoadAsync(new FakeVault([], new InvalidOperationException("Please run 'az login'.")), Ct);

        Assert.False(offline.IsLive);
        Assert.Null(offline.GitHubToken);
        Assert.Contains("az login", offline.LoadError); // shown in the banner, so the user knows what to do
    }

    [Fact]
    public async Task Secret_values_never_appear_when_settings_are_printed_or_logged()
    {
        var text = (await AppSettings.LoadAsync(new FakeVault(FullVault), Ct)).ToString();

        Assert.DoesNotContain("secret-value", text);
        Assert.Equal("AppSettings { Endpoint = https://example.cognitiveservices.azure.com/, GitHubToken = (set) }", text);
    }

    /// <summary>Opt-in: reads the real vault as the signed-in user. Asserts presence only; values are never printed.</summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_vault_supplies_everything_script_and_voice_need()
    {
        if (Environment.GetEnvironmentVariable("POAUTOROBO_LIVE") != "1") return;

        var settings = await AppSettings.LoadAsync(new KeyVaultSecretSource(KeyVaultSecretSource.DefaultVault, AppSettings.SignedInUser), Ct);

        Assert.Null(settings.LoadError);
        Assert.True(settings.IsLive);
    }
}
