using System.Globalization;
using System.Text.Json;

namespace PoAutoRobo.Core.Library;

/// <summary>
/// A running list, kept in the episode folder, of what the episode has cost. Only pictures are priced: they are the
/// one paid item whose price the app knows. Script and voice calls are listed by how much they used, without a price.
/// </summary>
public static class SpendLog
{
    public const string FileName = "spend.jsonl";

    private static readonly Lock Gate = new(); // recordings finish on background threads

    private sealed record Entry(DateTimeOffset When, string What, decimal Dollars, int Units = 0);

    /// <param name="units">How much was used when there is no price to give: tokens, characters.</param>
    public static void Add(string folder, string what, decimal dollars, int units = 0)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(new Entry(DateTimeOffset.Now, what, dollars, units)) + "\n");
        }
    }

    /// <summary>How much of <paramref name="what"/> has been used so far; zero when nothing is logged or the file cannot be read.</summary>
    public static int Units(string folder, string what) => (int)Sum(folder, entry => entry.What == what ? entry.Units : 0);

    /// <summary>Total so far; zero when nothing has been logged or the file cannot be read.</summary>
    public static decimal Total(string folder) => Sum(folder, entry => entry.Dollars);

    private static decimal Sum(string folder, Func<Entry, decimal> amount)
    {
        try
        {
            lock (Gate)
                return File.ReadLines(Path.Combine(folder, FileName)).Where(line => line.Length > 0).Sum(line => JsonSerializer.Deserialize<Entry>(line) is { } entry ? amount(entry) : 0);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return 0;
        }
    }

    public static string InWords(decimal dollars) => $"about ${dollars.ToString("0.00", CultureInfo.InvariantCulture)} of pictures";
}
