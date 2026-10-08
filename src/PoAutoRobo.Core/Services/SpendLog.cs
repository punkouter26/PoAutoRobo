using System.Globalization;
using System.Text.Json;

namespace PoAutoRobo.Core.Services;

/// <summary>
/// A running list, kept in the episode folder, of what the episode has cost. Only pictures are priced: they are the
/// one paid item whose price the app knows. Script and voice calls are not listed.
/// </summary>
public static class SpendLog
{
    public const string FileName = "spend.jsonl";

    private sealed record Entry(DateTimeOffset When, string What, decimal Dollars);

    public static void Add(string folder, string what, decimal dollars)
    {
        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(new Entry(DateTimeOffset.Now, what, dollars)) + "\n");
    }

    /// <summary>Total so far; zero when nothing has been logged or the file cannot be read.</summary>
    public static decimal Total(string folder)
    {
        try
        {
            return File.ReadLines(Path.Combine(folder, FileName)).Where(line => line.Length > 0).Sum(line => JsonSerializer.Deserialize<Entry>(line)?.Dollars ?? 0);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return 0;
        }
    }

    public static string InWords(decimal dollars) => $"about ${dollars.ToString("0.00", CultureInfo.InvariantCulture)} of pictures";
}
