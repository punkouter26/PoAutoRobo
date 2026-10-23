using Azure.Core;
using Azure.Identity;

namespace PoAutoRobo.Core.Library;

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

    // A deployment or model that may be left unnamed: null when neither the variable nor the fallback names one.
    private static string? Named(string variable, string fallback = "") => Choice(variable, fallback.Trim()) is { Length: > 0 } name ? name : null;

    public string ChatDeployment { get; init; } = Choice("POAUTOROBO_CHAT_MODEL", "gpt-5.4");
    public string FastChatDeployment { get; init; } = Choice("POAUTOROBO_FAST_CHAT_MODEL", "gpt-5.4-mini");
    public string Voice { get; init; } = Choice("POAUTOROBO_VOICE", "en-US-DavisNeural");

    /// <summary>"gpt-image-2", once that deployment exists, holds a character's look better.</summary>
    public string ImageDeployment { get; init; } = Choice("POAUTOROBO_IMAGE_MODEL", "gpt-image-1-mini");

    /// <summary>The video model's deployment (for example "sora-2"). Null until named: AI video is dear, so it is opt-in.</summary>
    public string? VideoDeployment { get; init; } = Named("POAUTOROBO_VIDEO_MODEL");

    /// <summary>
    /// The embedding model's deployment (for example "text-embedding-3-small"). Null until named; reference passages
    /// are then found by matching words alone.
    /// </summary>
    public string? EmbeddingDeployment { get; init; } = Named("POAUTOROBO_EMBEDDING_MODEL");

    /// <summary>
    /// A model run on this computer (for example "qwen3:8b" in Ollama) that does the small jobs for free: checking
    /// whether a picture still fits, search terms, teaser ideas and upload notes. Null until named.
    /// </summary>
    public string? LocalModel { get; init; } = Named("POAUTOROBO_LOCAL_MODEL");

    /// <summary>Where the local model answers, in the OpenAI style; Ollama's own address unless POAUTOROBO_LOCAL_URL says otherwise.</summary>
    public Uri LocalUrl { get; init; } = new(Choice("POAUTOROBO_LOCAL_URL", "http://localhost:11434/v1/").TrimEnd('/') + "/");

    // What script and voice cost depends on the user's own Azure agreement, so the app carries no price for them.
    // Each is null until given, and that service's use is then shown as an amount used, without a price.

    /// <summary>Rates of the main script model, from POAUTOROBO_CHAT_RATES as "input,cached input,output" in dollars a million tokens.</summary>
    public TokenRates? ChatRates { get; init; } = TokenRates.Parse(Choice("POAUTOROBO_CHAT_RATES", ""));

    /// <summary>The same for the fast model, from POAUTOROBO_FAST_CHAT_RATES.</summary>
    public TokenRates? FastChatRates { get; init; } = TokenRates.Parse(Choice("POAUTOROBO_FAST_CHAT_RATES", ""));

    /// <summary>Dollars for a million characters spoken, from POAUTOROBO_VOICE_RATE.</summary>
    public decimal? VoiceRate { get; init; } =
        decimal.TryParse(Choice("POAUTOROBO_VOICE_RATE", ""), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var rate) && rate >= 0 ? rate : null;

    /// <summary>Key for the Pexels stock photo library; null when there is none and stock photos are not offered.</summary>
    public string? PexelsKey { get; init; } = Named("POAUTOROBO_PEXELS_KEY");

    /// <summary>The variables that switch the optional services; one that is set wins over the switch in Settings.</summary>
    public static readonly string[] ServiceVariables = ["POAUTOROBO_VIDEO_MODEL", "POAUTOROBO_LOCAL_MODEL", "POAUTOROBO_EMBEDDING_MODEL"];

    /// <summary>
    /// These settings with the optional services as switched in Settings. A service's environment variable, when set,
    /// still names its model; stock photos switched off are off, whatever key there is.
    /// </summary>
    public AppSettings Using(ServiceChoices choices) => this with
    {
        VideoDeployment = Named("POAUTOROBO_VIDEO_MODEL", choices.Video ? choices.VideoModel : ""),
        LocalModel = Named("POAUTOROBO_LOCAL_MODEL", choices.Local ? choices.LocalModel : ""),
        EmbeddingDeployment = Named("POAUTOROBO_EMBEDDING_MODEL", choices.Meaning ? choices.EmbeddingModel : ""),
        PexelsKey = choices.StockPhotos ? PexelsKey : null,
    };

    /// <summary>Why the vault could not be read, in words fit to show the user.</summary>
    public string? LoadError { get; init; }

    public static async Task<AppSettings> LoadAsync(SecretSource vault, CancellationToken ct)
    {
        try
        {
            // Fetched together: each is a separate round trip made before the window can appear.
            var (endpoint, gitHub, pexels) = (Get("AzureOpenAI--Endpoint"), Get("GitHub--PAT"), Get("Pexels--ApiKey"));
            await Task.WhenAll(endpoint, gitHub, pexels);
            var settings = new AppSettings(Uri.TryCreate(await endpoint, UriKind.Absolute, out var uri) ? uri : null, await gitHub);
            return await pexels is { } key ? settings with { PexelsKey = key } : settings; // the environment variable stands when the vault has none
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Not signed in, no network, no permission: run on mocks and say why.
            return Offline with { LoadError = e.Message.Split('\n')[0].Trim() };
        }

        async Task<string?> Get(string name) =>
            await vault(name, ct) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    // Records print every property by default; secrets must never reach a log or an error message.
    public override string ToString() =>
        $"AppSettings {{ Endpoint = {Endpoint}, GitHubToken = {(GitHubToken is null ? "(none)" : "(set)")}, PexelsKey = {(PexelsKey is null ? "(none)" : "(set)")} }}";
}
