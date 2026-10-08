namespace PoAutoRobo.Core.Services;

/// <summary>Which services run for real. Anything not live uses its mock, and the app names those in its banner.</summary>
public sealed record ServicePlan(bool ScriptLive, bool VoiceLive)
{
    public IReadOnlyList<string> Simulated =>
        [.. new[] { (ScriptLive, "Script"), (VoiceLive, "Voice") }.Where(s => !s.Item1).Select(s => s.Item2)];
}

public static class ServiceSelector
{
    public static ServicePlan Plan(AppSettings settings) => new(
        ScriptLive: settings.Endpoint is not null && settings.ApiKey is not null,
        VoiceLive: settings.SpeechKey is not null && settings.SpeechRegion is not null);
}
