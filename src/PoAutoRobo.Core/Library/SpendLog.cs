using System.Globalization;
using System.Text.Json;

namespace PoAutoRobo.Core.Library;

/// <summary>One kind of spending on an episode, added up: what it was, what it cost, and how much was used.</summary>
public sealed record SpendLine(string What, decimal Dollars, int Units);

/// <summary>
/// A running list, kept in the episode folder, of what the episode has cost. Pictures and AI videos are priced from
/// the app's own price list; script and voice calls are priced when the user has given their rates, and are otherwise
/// listed by how much they used.
/// </summary>
public static class SpendLog
{
    public const string FileName = "spend.jsonl";

    // The names things are logged under. Each is written by one part of the app and added up by another.
    public const string Picture = "picture";
    public const string AiVideo = "AI video";
    public const string ScriptTokens = "script tokens";
    public const string CachedScriptTokens = "script tokens reused from cache";
    public const string VoiceCharacters = "voice characters";

    private static readonly Lock Gate = new(); // recordings finish on background threads

    private sealed record Entry(DateTimeOffset When, string What, decimal Dollars, int Units = 0);

    /// <param name="units">How much was used: tokens, characters. Zero when the line is a price alone.</param>
    public static void Add(string folder, string what, decimal dollars, int units = 0)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(new Entry(DateTimeOffset.Now, what, dollars, units)) + "\n");
        }
    }

    /// <summary>How much of <paramref name="what"/> has been used so far; zero when nothing is logged or the file cannot be read.</summary>
    public static int Units(string folder, string what) => Entries(folder).Where(entry => entry.What == what).Sum(entry => entry.Units);

    /// <summary>Total so far; zero when nothing has been logged or the file cannot be read.</summary>
    public static decimal Total(string folder) => Entries(folder).Sum(entry => entry.Dollars);

    /// <summary>The episode's spending by kind, dearest first.</summary>
    public static IReadOnlyList<SpendLine> Breakdown(string folder) =>
        [.. Entries(folder).GroupBy(entry => entry.What)
            .Select(group => new SpendLine(group.Key, group.Sum(entry => entry.Dollars), group.Sum(entry => entry.Units)))
            .OrderByDescending(line => line.Dollars).ThenBy(line => line.What, StringComparer.Ordinal)];

    /// <summary>What every episode under <paramref name="root"/> has cost in the calendar month <paramref name="now"/> falls in.</summary>
    public static decimal MonthTotal(string root, DateTimeOffset now) =>
        !Directory.Exists(root) ? 0 : Directory.GetDirectories(root).SelectMany(Entries)
            .Where(entry => entry.When.Year == now.Year && entry.When.Month == now.Month)
            .Sum(entry => entry.Dollars);

    private static List<Entry> Entries(string folder)
    {
        try
        {
            lock (Gate)
                return [.. File.ReadLines(Path.Combine(folder, FileName)).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<Entry>(line)).OfType<Entry>()];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return [];
        }
    }

    /// <summary>An amount of money as the app always writes it: "$0.06".</summary>
    public static string Money(decimal dollars) => "$" + dollars.ToString("0.00", CultureInfo.InvariantCulture);
}
