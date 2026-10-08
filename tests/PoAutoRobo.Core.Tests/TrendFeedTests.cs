using PoAutoRobo.Core.Services;

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

    private const string HackerNews = """
        { "hits": [
          { "title": "Unitree R1 teardown", "url": "https://example.com/teardown", "points": 212, "num_comments": 87, "created_at": "2026-10-04T12:00:00Z", "objectID": "1" },
          { "title": "Ask HN: humanoid robot sims?", "points": 40, "num_comments": 12, "created_at": "2026-10-03T12:00:00Z", "objectID": "42" },
          { "story_text": "a hit with no title, as the real service sometimes sends", "objectID": "43" }
        ] }
        """;

    [Fact]
    public void Atom_entries_become_cards_with_clean_text_and_their_link()
    {
        var card = TrendFeed.ParseFeed(Atom, "arXiv cs.RO")[0];

        Assert.Equal("Whole-Body Balance for Humanoid Robots", card.Title);
        Assert.Equal("We train a humanoid policy on the Unitree R1 with domain randomization.", card.Summary);
        Assert.Equal("arXiv cs.RO", card.Source);
        Assert.Equal("http://arxiv.org/abs/2610.00001v1", card.Url);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero), card.Published);
        Assert.Null(card.Interest);
    }

    [Fact]
    public void Rss_items_are_read_the_same_way()
    {
        var card = Assert.Single(TrendFeed.ParseFeed(Rss, "Outlet"));

        Assert.Equal("Unitree ships R1 update", card.Title);
        Assert.Equal("The R1 humanoid gets new firmware.", card.Summary);
        Assert.Equal("https://example.org/r1", card.Url);
    }

    [Fact]
    public void Hacker_news_stories_carry_points_and_comments_and_fall_back_to_the_discussion_link()
    {
        var cards = TrendFeed.ParseHackerNews(HackerNews);

        Assert.Equal("212 points · 87 comments", cards[0].Interest);
        Assert.Equal("https://example.com/teardown", cards[0].Url);
        Assert.Equal("https://news.ycombinator.com/item?id=42", cards[1].Url);
        Assert.Equal("Hacker News", cards[1].Source);
    }

    [Fact]
    public void Long_summaries_are_shortened()
    {
        var xml = Rss.Replace("The R1 humanoid gets new firmware.", "R1 " + new string('x', 600));

        Assert.InRange(TrendFeed.ParseFeed(xml, "Outlet")[0].Summary.Length, 1, TrendFeed.MaxSummaryLength + 1);
    }

    [Theory]
    [InlineData("Unitree R1 learns to walk", "", true)]
    [InlineData("New firmware for the R1", "Unitree shipped an update for its smallest humanoid.", true)]
    [InlineData("Unitree's R1: a teardown", "", true)]
    [InlineData("Whole-body balance for humanoid robots", "A general method for bipeds.", false)]   // humanoids in general
    [InlineData("Unitree G1 does a backflip", "", false)]                                              // another Unitree robot
    [InlineData("Rivian R1 road test", "An electric truck.", false)]                                   // another maker's R1
    [InlineData("Unitree shows the R1S prototype", "", false)]                                         // R1 must be the whole word
    public void Only_cards_about_the_unitree_r1_itself_are_kept(string title, string summary, bool kept)
    {
        Assert.Equal(kept, TrendFeed.IsAboutR1(new TopicCard(title, summary, "x", "https://example.org", null, DateTimeOffset.MinValue)));
    }

    [Fact]
    public async Task A_general_humanoid_story_no_longer_reaches_the_radar()
    {
        var feed = new TrendFeed((url, _) => Task.FromResult(url.Contains("hn.algolia") ? HackerNews : Rss));

        var cards = await feed.GetAsync(Ct);

        Assert.DoesNotContain(cards, c => c.Title.Contains("Ask HN"));
        Assert.Contains(cards, c => c.Title == "Unitree R1 teardown");
    }

    [Fact]
    public async Task Cards_are_merged_newest_first_and_only_relevant_ones_are_kept()
    {
        var feed = new TrendFeed((url, _) => Task.FromResult(url.Contains("hn.algolia") ? HackerNews : url.Contains("arxiv") ? Atom : Rss));

        var cards = await feed.GetAsync(Ct);

        Assert.DoesNotContain(cards, c => c.Title.Contains("Warehouse"));
        Assert.Equal(cards.OrderByDescending(c => c.Published), cards);
        Assert.Contains(cards, c => c.Source == "Hacker News");
        Assert.Contains(cards, c => c.Source == "arXiv cs.RO");
        Assert.Equal(cards.Count, cards.Select(c => c.Url).Distinct().Count());
    }

    [Fact]
    public async Task One_dead_or_garbled_source_does_not_empty_the_list()
    {
        var feed = new TrendFeed((url, _) =>
            url.Contains("hn.algolia") ? Task.FromResult(HackerNews)
            : url.Contains("arxiv") ? Task.FromResult("<html>Service unavailable</html>")
            : Task.FromException<string>(new HttpRequestException("No such host")));

        var cards = await feed.GetAsync(Ct);

        Assert.NotEmpty(cards);
        Assert.All(cards, c => Assert.Equal("Hacker News", c.Source));
    }

    [Fact]
    public async Task With_every_source_down_sample_topics_are_offered_and_labelled_as_samples()
    {
        var feed = new TrendFeed((_, _) => Task.FromException<string>(new HttpRequestException("offline")));

        var cards = await feed.GetAsync(Ct);

        Assert.NotEmpty(cards);
        Assert.All(cards, c => Assert.Equal("Sample topic", c.Source));
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
