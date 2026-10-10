namespace PoAutoRobo.Core.Library;

/// <summary>What the script model charges, in US dollars for a million tokens of each sort.</summary>
/// <param name="CachedInput">Input the service had seen before and charges less for.</param>
public sealed record TokenRates(decimal Input, decimal CachedInput, decimal Output)
{
    public decimal Cost(int input, int cached, int output) =>
        ((input - cached) * Input + cached * CachedInput + output * Output) / 1_000_000m;

    /// <summary>Reads "input,cached input,output", for example "1.25,0.125,10"; null when the text is not three amounts.</summary>
    public static TokenRates? Parse(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        var amounts = parts.Select(p => decimal.TryParse(p, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount) && amount >= 0 ? amount : (decimal?)null).ToArray();
        return amounts is [{ } input, { } cached, { } output] ? new TokenRates(input, cached, output) : null;
    }
}
