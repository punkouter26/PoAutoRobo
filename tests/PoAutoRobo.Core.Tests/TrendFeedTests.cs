
namespace PoAutoRobo.Core.Tests;

public sealed class TrendFeedTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private const string Atom = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>arXiv</title>
          <entry>
            <id>http://arxiv.org/abs/2610.00001v1</id>
            <title>Whole-Body Balance for Humanoid
              Robots</title>
            <summary>We train a &lt;b&gt;humanoid&lt;/b&gt; policy on the Unitree R1 with domain randomization.</summary>
            <updated>2026-10-05T10:00:00Z</updated>
            <link href="http://arxiv.org/abs/2610.00001v1" rel="alternate" />
          </entry>
          <entry>
            <id>http://arxiv.org/abs/2610.00002v1</id>
            <title>Grasp Planning for Warehouse Arms</title>
            <summary>Nothing about legs here.</summary>
            <updated>2026-10-06T10:00:00Z</updated>
            <link href="http://arxiv.org/abs/2610.00002v1" rel="alternate" />
          </entry>
        </feed>
        """;

    private const string Rss = """
        <?xml version="1.0"?>
        <rss version="2.0"><channel><title>Outlet</title>
          <item><title>Unitree ships R1 update</title><link>https://example.org/r1</link>
            <description>&lt;p&gt;The R1 humanoid gets new firmware.&lt;/p&gt;</description><pubDate>Tue, 06 Oct 2026 08:00:00 GMT</pubDate></item>
        </channel></rss>
        """;

    // The last story is about the R1 but its link would open something on the machine instead of a web page.
    private const string HackerNews = """
        { "hits": [
          { "title": "Unitree R1 teardown", "url": "https://example.com/teardown", "points": 212, "num_comments": 87, "created_at": "2026-10-04T12:00:00Z", "objectID": "1" },
          { "title": "Ask HN: humanoid robot sims?", "points": 40, "num_comments": 12, "created_at": "2026-10-03T12:00:00Z", "objectID": "42" },
          { "story_text": "a hit with no title, as the real service sometimes sends", "objectID": "43" },
          { "title": "Unitree R1 firmware", "url": "search-ms:query=evil", "points": 9, "num_comments": 1, "created_at": "2026-10-07T12:00:00Z", "objectID": "44" }
        ] }
        """;

    [Fact]
    public void Atom_rss_and_hacker_news_items_become_cards_with_clean_text_their_link_and_where_there_is_one_an_interest_figure()
    {
        var atom = TrendFeed.ParseFeed(Atom, "arXiv cs.RO")[0];

        Assert.Equal("Whole-Body Balance for Humanoid Robots", atom.Title);
        Assert.Equal("We train a humanoid policy on the Unitree R1 with domain randomization.", atom.Summary);
        Assert.Equal("arXiv cs.RO", atom.Source);
        Assert.Equal("http://arxiv.org/abs/2610.00001v1", atom.Url);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero), atom.Published);
        Assert.Null(atom.Interest);

        var rss = Assert.Single(TrendFeed.ParseFeed(Rss, "Outlet"));

        Assert.Equal("Unitree ships R1 update", rss.Title);
        Assert.Equal("The R1 humanoid gets new firmware.", rss.Summary);
        Assert.Equal("https://example.org/r1", rss.Url);

        var longOne = TrendFeed.ParseFeed(Rss.Replace("The R1 humanoid gets new firmware.", "R1 " + new string('x', 600)), "Outlet")[0];

        Assert.InRange(longOne.Summary.Length, 1, TrendFeed.MaxSummaryLength + 1);

        // Hacker News is the one source with an interest figure; a text post falls back to its discussion link.
        var cards = TrendFeed.ParseHackerNews(HackerNews);

        Assert.Equal(3, cards.Count); // the hit with no title is left out
        Assert.Equal("212 points · 87 comments", cards[0].Interest);
        Assert.Equal("https://example.com/teardown", cards[0].Url);
        Assert.Equal("https://news.ycombinator.com/item?id=42", cards[1].Url);
        Assert.Equal("Hacker News", cards[1].Source);
    }

    [Theory]
    [InlineData("New firmware for the R1", "Unitree shipped an update for its smallest humanoid.", true)]
    [InlineData("Rivian R1 road test", "An electric truck.", false)]                       // another maker's R1
    [InlineData("Unitree shows the R1S prototype", "", false)]                             // R1 must be the whole word
    public void Only_cards_about_the_unitree_r1_itself_are_kept(string title, string summary, bool kept)
    {
        Assert.Equal(kept, TrendFeed.IsAboutR1(new TopicCard(title, summary, "x", "https://example.org", null, DateTimeOffset.MinValue)));
    }

    // Links from feeds and saved files end up on a button the user clicks, so only web addresses may get through.
    [Fact]
    public void Only_web_links_are_accepted()
    {
        Assert.True(TrendFeed.IsWebLink("https://example.com/r1"));
        Assert.True(TrendFeed.IsWebLink("http://example.com/r1"));
        // Protocol handlers, script, local files, file shares and things that are not addresses at all.
        foreach (var url in new[] { "search-ms:query=x", "ms-msdt:/id x", "javascript:alert(1)", "file:///C:/Windows/win.ini", @"\\attacker\share\x", "not a url", "" })
            Assert.False(TrendFeed.IsWebLink(url), url);
    }

    [Fact]
    public async Task For_any_topic_episodes_the_radar_shows_popular_stories_unfiltered_but_still_only_web_links_and_nothing_when_the_source_is_down()
    {
        var feed = new TrendFeed((_, _) => Task.FromResult(HackerNews));

        var cards = await feed.GetGeneralAsync(Ct);

        Assert.Contains(cards, c => c.Title == "Ask HN: humanoid robot sims?"); // not about the R1, and kept
        Assert.DoesNotContain(cards, c => c.Title == "Unitree R1 firmware");    // its link is not a web address
        Assert.Empty(await new TrendFeed((_, _) => throw new HttpRequestException("down")).GetGeneralAsync(Ct));

        // A topic the user has typed is looked up in an encyclopedia and in the news: made safe for an address, cut when
        // it is a whole pasted page, and with the filler words left out of the news search, which wants every word matched.
        var asked = new List<string>();
        const string wikipedia = """{ "query": { "search": [ { "title": "Hamburger (food)", "snippet": "A <span class=\"searchmatch\">burger</span> is grilled &amp; served." } ] } }""";
        var search = new TrendFeed((url, _) =>
        {
            lock (asked) asked.Add(url);
            return Task.FromResult(url.Contains("wikipedia") ? wikipedia : HackerNews);
        });

        var found = await search.SearchAsync("how to grill\n a burger & " + new string('x', 500), Ct);

        Assert.Equal(("Hamburger (food)", "A burger is grilled & served.", "https://en.wikipedia.org/wiki/Hamburger_%28food%29"), (found[0].Title, found[0].Summary, found[0].Url));
        Assert.Contains(found, c => c.Title == "Ask HN: humanoid robot sims?");
        Assert.Contains(asked, url => url.Contains("srsearch=how%20to%20grill%20a%20burger%20%26%20xxx"));
        Assert.Contains(asked, url => url.Contains("query=grill%20burger%20xxx"));
        Assert.All(asked, url => Assert.True(url.Length < 350));
        // One source down leaves the other's results; both down leaves none.
        Assert.Single(await new TrendFeed((url, _) => url.Contains("wikipedia") ? Task.FromResult(wikipedia) : throw new HttpRequestException("down")).SearchAsync("burgers", Ct));
        Assert.Empty(await new TrendFeed((_, _) => throw new HttpRequestException("down")).SearchAsync("burgers", Ct));
    }

    [Fact]
    public async Task Cards_are_merged_newest_first_and_only_relevant_ones_with_web_links_reach_the_radar()
    {
        var feed = new TrendFeed((url, _) => Task.FromResult(url.Contains("hn.algolia") ? HackerNews : url.Contains("arxiv") ? Atom : Rss));

        var cards = await feed.GetAsync(Ct);

        Assert.Equal(["Unitree ships R1 update", "Whole-Body Balance for Humanoid Robots", "Unitree R1 teardown"], cards.Select(c => c.Title));
        Assert.Equal(["IEEE Spectrum", "arXiv cs.RO", "Hacker News"], cards.Select(c => c.Source)); // the same story from two outlets is listed once
        Assert.All(cards, c => Assert.True(TrendFeed.IsWebLink(c.Url), c.Url)); // the search-ms story never gets through
    }

    [Fact]
    public async Task One_dead_or_garbled_source_does_not_empty_the_list_and_with_every_source_down_sample_topics_are_offered()
    {
        var partly = new TrendFeed((url, _) =>
            url.Contains("hn.algolia") ? Task.FromResult(HackerNews)
            : url.Contains("arxiv") ? Task.FromResult("<html>Service unavailable</html>")
            : Task.FromException<string>(new HttpRequestException("No such host")));
        var offline = new TrendFeed((_, _) => Task.FromException<string>(new HttpRequestException("offline")));

        Assert.Equal("Hacker News", Assert.Single(await partly.GetAsync(Ct)).Source);

        var samples = await offline.GetAsync(Ct);
        Assert.NotEmpty(samples);
        Assert.All(samples, c => Assert.Equal("Sample topic", c.Source)); // labelled, so they are not mistaken for news
    }

    /// <summary>Opt-in: the real feeds. Reports which sources answered.</summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_feeds_return_real_cards_and_every_one_is_about_the_unitree_r1()
    {
        if (Environment.GetEnvironmentVariable("POAUTOROBO_LIVE") != "1") return;

        var cards = await TrendFeed.Create().GetAsync(Ct);

        var sources = cards.GroupBy(c => c.Source).Select(g => $"{g.Key} ({g.Count()})").ToList();
        Assert.All(cards, c => Assert.True(TrendFeed.IsAboutR1(c), c.Title));
        Assert.True(cards.Count > 0 && cards.All(c => c.Source != "Sample topic") && Environment.GetEnvironmentVariable("POAUTOROBO_SHOW") is null, "real cards: " + string.Join(", ", sources));
    }
}
