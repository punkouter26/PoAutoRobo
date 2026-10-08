using System.Net;
using System.ServiceModel.Syndication;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace PoAutoRobo.Core.Services;

/// <param name="Interest">Community interest in the source's own terms, or null when the source has none.</param>
public sealed record TopicCard(string Title, string Summary, string Source, string Url, string? Interest, DateTimeOffset Published);

/// <summary>Downloads a URL as text.</summary>
public delegate Task<string> FetchText(string url, CancellationToken ct);

/// <summary>Recent topics about the Unitree R1 from free public feeds. Any source may fail without affecting the others.</summary>
public sealed partial class TrendFeed(FetchText fetch)
{
    public const int MaxSummaryLength = 220;
    private const int MaxCards = 20;

    // ponytail: X/Twitter, Reddit and GitHub activity are left out. X is paid, Reddit refuses anonymous readers,
    // and commit or release titles do not read as video topics. Add a row here to add a source.
    private static readonly (string Name, string Url, bool IsHackerNews)[] Sources =
    [
        ("arXiv cs.RO", "https://export.arxiv.org/api/query?search_query=all:%22Unitree+R1%22&sortBy=submittedDate&sortOrder=descending&max_results=20", false),
        ("Hacker News", "https://hn.algolia.com/api/v1/search_by_date?query=unitree+r1&tags=story&hitsPerPage=30", true),
        ("IEEE Spectrum", "https://spectrum.ieee.org/feeds/topic/robotics.rss", false),
        ("The Robot Report", "https://www.therobotreport.com/feed/", false),
    ];
    private static readonly TopicCard[] Samples =
    [
        new("Training whole-body balance on the Unitree R1", "How a humanoid learns to stay upright in simulation before it ever stands on a lab floor.", "Sample topic", "https://github.com/unitreerobotics/unitree_rl_mjlab", null, DateTimeOffset.MinValue),
        new("From simulation to the real robot", "What changes when a walking policy leaves MuJoCo and meets real motors, friction and delay.", "Sample topic", "https://github.com/unitreerobotics/unitree_mujoco", null, DateTimeOffset.MinValue),
        new("Programming the R1 with the Unitree SDK", "The control loop, the messages and the safety habits behind a first motion program.", "Sample topic", "https://github.com/unitreerobotics/unitree_sdk2", null, DateTimeOffset.MinValue),
    ];

    public static TrendFeed Create()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 4 * 1024 * 1024 }; // feeds are small; refuse anything huge
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PoAutoRobo/1.0"); // several feeds refuse requests with no user agent
        return new TrendFeed((url, ct) => http.GetStringAsync(url, ct));
    }

    public async Task<IReadOnlyList<TopicCard>> GetAsync(CancellationToken ct)
    {
        var batches = await Task.WhenAll(Sources.Select(async source =>
        {
            try
            {
                var text = await fetch(source.Url, ct);
                return source.IsHackerNews ? ParseHackerNews(text) : ParseFeed(text, source.Name);
            }
            catch (Exception e) when (e is HttpRequestException or XmlException or JsonException or TaskCanceledException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                ct.ThrowIfCancellationRequested();
                return [];
            }
        }));

        var cards = batches.SelectMany(b => b)
            .Where(IsAboutR1)
            .Where(c => IsWebLink(c.Url)) // the user clicks these: nothing but web links may get through
            .DistinctBy(c => c.Url)
            .OrderByDescending(c => c.Published)
            .Take(MaxCards)
            .ToList();
        return cards.Count > 0 ? cards : Samples;
    }

    /// <summary>
    /// True only for the Unitree R1 itself: the card must name both the maker and the model. General humanoid news,
    /// other Unitree robots and other makers' "R1" products are all left out.
    /// </summary>
    // Feed links end up on a button the user clicks. Anything other than a web address (file shares, search-ms:,
    // other protocol handlers) could make that click do something on the machine, so it is refused.
    public static bool IsWebLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public static bool IsAboutR1(TopicCard card)
    {
        var text = $"{card.Title} {card.Summary}";
        return text.Contains("unitree", StringComparison.OrdinalIgnoreCase) && WholeWordR1().IsMatch(text);
    }

    /// <summary>Reads an RSS or Atom document.</summary>
    public static IReadOnlyList<TopicCard> ParseFeed(string xml, string source)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        return [.. SyndicationFeed.Load(reader).Items.Select(item => new TopicCard(
            Clean(item.Title?.Text),
            Shorten(Clean(item.Summary?.Text)),
            source,
            (item.Links.FirstOrDefault(l => l.RelationshipType is null or "alternate") ?? item.Links.FirstOrDefault())?.Uri.ToString() ?? item.Id ?? "",
            null,
            item.PublishDate > DateTimeOffset.MinValue ? item.PublishDate : item.LastUpdatedTime))];
    }

    public static IReadOnlyList<TopicCard> ParseHackerNews(string json)
    {
        // The service omits fields freely (text posts have no url, some hits no title), so nothing is assumed present.
        static string? Text(JsonElement hit, string name) => hit.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        static int Number(JsonElement hit, string name) => hit.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.GetProperty("hits").EnumerateArray()
            .Where(hit => Text(hit, "title") is { Length: > 0 })
            .Select(hit => new TopicCard(
                Clean(Text(hit, "title")),
                "",
                "Hacker News",
                Text(hit, "url") is { Length: > 0 } url ? url : $"https://news.ycombinator.com/item?id={Text(hit, "objectID")}",
                $"{Number(hit, "points")} points · {Number(hit, "num_comments")} comments",
                DateTimeOffset.TryParse(Text(hit, "created_at"), out var created) ? created : DateTimeOffset.MinValue))];
    }

    /// <summary>Plain single-line text: feeds carry HTML and hard line breaks.</summary>
    private static string Clean(string? text) =>
        Whitespace().Replace(WebUtility.HtmlDecode(Tags().Replace(text ?? "", " ")), " ").Trim();

    private static string Shorten(string text) => text.Length <= MaxSummaryLength ? text : text[..MaxSummaryLength].TrimEnd() + "…";

    [GeneratedRegex(@"\bR1\b", RegexOptions.IgnoreCase)]
    private static partial Regex WholeWordR1();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
