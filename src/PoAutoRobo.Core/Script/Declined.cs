using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoAutoRobo.Core.Script;

/// <summary>What said no to a request for words.</summary>
public enum DeclinedBy
{
    /// <summary>The service's safety filter read the request and never passed it to the model.</summary>
    RequestFilter,

    /// <summary>The service's safety filter stopped the model's reply partway.</summary>
    ReplyFilter,

    /// <summary>The model itself would not write it.</summary>
    Model,
}

/// <summary>
/// A request for words was turned away on grounds of content, not because anything is broken. Saying which of the
/// three things did it, and over what, is what tells the user (and the app) what to try next: a filter is a setting
/// on the Azure deployment, and a model's own refusal is not.
/// </summary>
/// <param name="flagged">What the filter objected to and how strongly, for example "violence: medium"; null when it did not say or a model declined.</param>
/// <param name="model">The deployment or model that was asked.</param>
public sealed partial class ScriptDeclinedException(DeclinedBy by, string? flagged, string model)
    : InvalidOperationException(InWords(by, flagged, model) + " Reword the topic and try again.")
{
    public DeclinedBy By { get; } = by;

    public string? Flagged { get; } = flagged;

    public string Model { get; } = model;

    /// <summary>What happened in one sentence, without the advice.</summary>
    public string What { get; } = InWords(by, flagged, model);

    private static string InWords(DeclinedBy by, string? flagged, string model)
    {
        var over = flagged is null ? "" : $" ({flagged})";
        return by switch
        {
            DeclinedBy.RequestFilter => $"The safety filter on {model} blocked the request before the model saw it{over}. The filter's strictness is a setting on that deployment in Azure.",
            DeclinedBy.ReplyFilter => $"The safety filter on {model} stopped the reply partway{over}. The filter's strictness is a setting on that deployment in Azure.",
            _ => $"{model} declined to write this.",
        };
    }

    /// <summary>
    /// Reads a failed reply from the Azure AI service. When it was the safety filter that refused, returns what it
    /// objected to ("violence: medium", or "" when it did not say); null when the failure was something else.
    /// </summary>
    public static string? FilterVerdict(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                return null;
            var code = error.TryGetProperty("code", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : null;
            // The service has spelled both of these more than one way over its versions.
            var inner = new[] { "innererror", "inner_error" }.Select(name => error.TryGetProperty(name, out var found) ? found : default).FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
            var results = inner.ValueKind == JsonValueKind.Object
                ? new[] { "content_filter_result", "content_filter_results" }.Select(name => inner.TryGetProperty(name, out var found) ? found : default).FirstOrDefault(e => e.ValueKind == JsonValueKind.Object)
                : default;
            if (results.ValueKind != JsonValueKind.Object && code is not ("content_filter" or "contentFilter" or "content_policy_violation" or "moderation_blocked"))
                return null;
            return results.ValueKind != JsonValueKind.Object ? "" : string.Join(", ", results.EnumerateObject()
                .Where(category => category.Value.ValueKind == JsonValueKind.Object && category.Value.TryGetProperty("filtered", out var filtered) && filtered.ValueKind == JsonValueKind.True)
                .Select(category => category.Value.TryGetProperty("severity", out var severity) ? $"{category.Name.Replace('_', ' ')}: {severity.GetString()}" : category.Name.Replace('_', ' ')));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when words that were asked for are a refusal to write them. Some models answer in exactly the shape asked,
    /// with "I cannot assist with that" where the script should be, and it would otherwise be taken for a script.
    /// </summary>
    public static bool IsRefusal(string text) => Refusal().IsMatch(text);

    [GeneratedRegex(@"^\W*(I\s+(cannot|can['’]?t|won['’]?t|will not|am (not able|unable)|['’]m (not able|unable|sorry))\b|(I['’]m\s+)?sorry,?\s+(but\s+)?I\b|(I\s+)?(must|have to)\s+decline\b|I\s+apologi[sz]e,?\s+but\b)", RegexOptions.IgnoreCase)]
    private static partial Regex Refusal();
}
