namespace PoAutoRobo.Core.Services;

/// <summary>
/// Whether the paid services run for real. Script, voice and pictures all come from the one Azure AI resource, so
/// they are live or simulated together; the app names the simulated ones in its banner.
/// </summary>
public sealed record ServicePlan(bool Live)
{
    public IReadOnlyList<string> Simulated => Live ? [] : ["Script", "Voice"];
}

public static class ServiceSelector
{
    public static ServicePlan Plan(AppSettings settings) => new(settings.Endpoint is not null && settings.ApiKey is not null);
}
