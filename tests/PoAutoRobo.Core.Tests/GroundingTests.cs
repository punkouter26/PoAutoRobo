using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

public sealed class GroundingTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _cache = Directory.CreateTempSubdirectory("poautorobo-").FullName;

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    private const string MjlabReadme = """
        # unitree_rl_mjlab

        Reinforcement learning for Unitree robots on MuJoCo. Supported robots: Go2, G1, H1_2 and R1.

        ## Training

        Run the training script to train a walking policy for the R1. Rewards include velocity tracking and balance terms.

        ## License

        BSD 3-Clause. See the license file for the full text of the terms and conditions that apply.
        """;

    private static RepoDocument Doc(string repo, string text) => new(repo, "README.md", $"https://github.com/{repo}/blob/main/README.md", text);

    private static RepoSource Serving(Dictionary<string, string> readmes, List<string>? asked = null) => (repo, _, _) =>
    {
        asked?.Add(repo);
        return Task.FromResult<IReadOnlyList<RepoDocument>>(readmes.TryGetValue(repo, out var text) ? [Doc(repo, text)] : []);
    };

    [Fact]
    public void Keywords_drop_filler_words_and_keep_short_model_names()
    {
        Assert.Equal(["training", "balance", "r1", "edu", "isaac", "lab"], Grounding.Keywords("Training the balance of an R1 EDU using Isaac Lab"));
    }

    [Fact]
    public void Documents_are_split_at_headings_and_keep_their_source()
    {
        var chunks = Grounding.Chunk(Doc("unitreerobotics/unitree_rl_mjlab", MjlabReadme));

        Assert.Equal(3, chunks.Count);
        Assert.StartsWith("## Training", chunks[1].Text);
        Assert.All(chunks, c => Assert.Equal("https://github.com/unitreerobotics/unitree_rl_mjlab/blob/main/README.md", c.Url));
    }

    [Fact]
    public void Long_sections_are_cut_to_a_readable_size()
    {
        var chunks = Grounding.Chunk(Doc("a/b", "# Big\n\n" + new string('x', 5000)));

        Assert.All(chunks, c => Assert.InRange(c.Text.Length, 1, Grounding.MaxSnippetLength));
    }

    [Fact]
    public async Task Sections_matching_more_topic_words_come_first_and_unrelated_ones_are_left_out()
    {
        var grounding = new Grounding(Serving(new() { ["unitreerobotics/unitree_rl_mjlab"] = MjlabReadme }), _cache);

        var snippets = await grounding.FindAsync("Training a walking policy with balance rewards", Ct);

        Assert.StartsWith("## Training", snippets[0].Text);
        Assert.DoesNotContain(snippets, s => s.Text.StartsWith("## License"));
    }

    [Fact]
    public async Task Ranking_is_the_same_every_time()
    {
        var grounding = new Grounding(Serving(new() { ["unitreerobotics/unitree_rl_mjlab"] = MjlabReadme, ["unitreerobotics/unitree_sdk2"] = MjlabReadme }), _cache);

        var first = await grounding.FindAsync("R1 training", Ct);
        var second = await grounding.FindAsync("R1 training", Ct);

        Assert.Equal(first, second);
        Assert.Equal("unitreerobotics/unitree_sdk2", first[0].Repo); // ties keep the fixed repository order
    }

    [Fact]
    public async Task Only_the_official_repositories_are_asked()
    {
        var asked = new List<string>();

        await new Grounding(Serving([], asked), _cache).FindAsync("anything", Ct);

        Assert.Equal(Grounding.OfficialRepos, asked);
        Assert.Contains("unitreerobotics/unitree_rl_mjlab", asked);
        Assert.Contains("isaac-sim/IsaacLab", asked);
    }

    [Fact]
    public async Task When_github_refuses_the_last_good_copy_is_used()
    {
        await new Grounding(Serving(new() { ["unitreerobotics/unitree_rl_mjlab"] = MjlabReadme }), _cache).FindAsync("R1 training", Ct);
        RepoSource rateLimited = (_, _, _) => throw new HttpRequestException("API rate limit exceeded");

        var snippets = await new Grounding(rateLimited, _cache).FindAsync("R1 training", Ct);

        Assert.NotEmpty(snippets);
        Assert.Equal("unitreerobotics/unitree_rl_mjlab", snippets[0].Repo);
    }

    [Fact]
    public async Task With_no_network_and_no_saved_copy_there_are_simply_no_snippets()
    {
        RepoSource offline = (_, _, _) => throw new HttpRequestException("No such host");

        Assert.Empty(await new Grounding(offline, _cache).FindAsync("R1 training", Ct));
    }

    /// <summary>Opt-in: real GitHub.</summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_github_returns_snippets_from_official_repositories()
    {
        if (Environment.GetEnvironmentVariable("POAUTOROBO_LIVE") != "1") return;
        var settings = await AppSettings.LoadAsync(new KeyVaultSecretSource(KeyVaultSecretSource.DefaultVault), Ct);

        var snippets = await Grounding.Create(settings.GitHubToken, _cache).FindAsync("Training a walking policy for the R1 in MuJoCo", Ct);

        Assert.NotEmpty(snippets);
        Assert.All(snippets, s => Assert.Contains(s.Repo, Grounding.OfficialRepos));
        Assert.All(snippets, s => Assert.StartsWith("https://github.com/", s.Url));
    }
}
