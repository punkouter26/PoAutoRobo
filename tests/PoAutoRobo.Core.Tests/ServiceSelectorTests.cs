using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

public sealed class ServiceSelectorTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly Dictionary<string, string> FullVault = new()
    {
        ["AzureOpenAI--Endpoint"] = "https://example.cognitiveservices.azure.com/",
        ["AzureOpenAI--ApiKey"] = "openai-secret-value",
        ["AzureSpeech-SubscriptionKey"] = "speech-secret-value",
        ["AzureSpeech-Region"] = "eastus2",
        ["GitHub--PAT"] = "github-secret-value",
    };

    private sealed class FakeVault(Dictionary<string, string> secrets, Exception? failure = null) : ISecretSource
    {
        public Task<string?> GetAsync(string name, CancellationToken ct) =>
            failure is not null ? Task.FromException<string?>(failure) : Task.FromResult(secrets.GetValueOrDefault(name));
    }

    [Fact]
    public async Task Vault_secrets_map_onto_settings()
    {
        var settings = await AppSettings.LoadAsync(new FakeVault(FullVault), Ct);

        Assert.Equal(new Uri("https://example.cognitiveservices.azure.com/"), settings.Endpoint);
        Assert.Equal("openai-secret-value", settings.ApiKey);
        Assert.Equal("speech-secret-value", settings.SpeechKey);
        Assert.Equal("eastus2", settings.SpeechRegion);
        Assert.Equal("github-secret-value", settings.GitHubToken);
        Assert.Null(settings.LoadError);
    }

    [Fact]
    public async Task With_everything_present_script_and_voice_are_live_and_nothing_is_simulated()
    {
        var plan = ServiceSelector.Plan(await AppSettings.LoadAsync(new FakeVault(FullVault), Ct));

        Assert.True(plan.ScriptLive);
        Assert.True(plan.VoiceLive);
        Assert.Empty(plan.Simulated);
    }

    [Theory]
    [InlineData("AzureOpenAI--ApiKey", "Script")]
    [InlineData("AzureOpenAI--Endpoint", "Script")]
    [InlineData("AzureSpeech-SubscriptionKey", "Voice")]
    [InlineData("AzureSpeech-Region", "Voice")]
    public async Task A_missing_or_blank_secret_falls_back_to_the_mock_for_that_service_only(string missing, string simulated)
    {
        var vault = new Dictionary<string, string>(FullVault) { [missing] = "  " };

        var plan = ServiceSelector.Plan(await AppSettings.LoadAsync(new FakeVault(vault), Ct));

        Assert.Equal([simulated], plan.Simulated);
    }

    [Fact]
    public async Task An_unreachable_vault_means_everything_is_simulated_and_the_reason_is_kept()
    {
        var settings = await AppSettings.LoadAsync(new FakeVault([], new InvalidOperationException("Please run 'az login'.")), Ct);

        Assert.Equal(["Script", "Voice"], ServiceSelector.Plan(settings).Simulated);
        Assert.Contains("az login", settings.LoadError);
    }

    [Fact]
    public async Task A_malformed_endpoint_is_treated_as_missing()
    {
        var vault = new Dictionary<string, string>(FullVault) { ["AzureOpenAI--Endpoint"] = "not a url" };

        Assert.False(ServiceSelector.Plan(await AppSettings.LoadAsync(new FakeVault(vault), Ct)).ScriptLive);
    }

    [Fact]
    public async Task Secret_values_never_appear_when_settings_are_printed_or_logged()
    {
        var text = (await AppSettings.LoadAsync(new FakeVault(FullVault), Ct)).ToString();

        Assert.DoesNotContain("secret-value", text);
        Assert.Contains("example.cognitiveservices.azure.com", text);
    }

    /// <summary>Opt-in: reads the real vault as the signed-in user. Asserts presence only; values are never printed.</summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_vault_supplies_everything_script_and_voice_need()
    {
        if (Environment.GetEnvironmentVariable("POAUTOROBO_LIVE") != "1") return;

        var settings = await AppSettings.LoadAsync(new KeyVaultSecretSource(KeyVaultSecretSource.DefaultVault), Ct);

        Assert.Null(settings.LoadError);
        Assert.Empty(ServiceSelector.Plan(settings).Simulated);
    }

    [Fact]
    public void Offline_settings_simulate_everything()
    {
        Assert.Equal(["Script", "Voice"], ServiceSelector.Plan(AppSettings.Offline).Simulated);
    }
}
