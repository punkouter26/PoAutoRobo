
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
    public void Topics_become_keywords_without_filler_and_documents_are_split_at_headings_and_cut_to_a_readable_size()
    {
        Assert.Equal(["training", "balance", "r1", "edu", "isaac", "lab"], Grounding.Keywords("Training the balance of an R1 EDU using Isaac Lab"));

        var chunks = Grounding.Chunk(Doc("unitreerobotics/unitree_rl_mjlab", MjlabReadme));

        Assert.Equal(3, chunks.Count);
        Assert.StartsWith("## Training", chunks[1].Text);
        Assert.All(chunks, c => Assert.Equal("https://github.com/unitreerobotics/unitree_rl_mjlab/blob/main/README.md", c.Url));
        Assert.Equal(Grounding.MaxSnippetLength, Assert.Single(Grounding.Chunk(Doc("a/b", "# Big\n\n" + new string('x', 5000)))).Text.Length);
    }

    [Fact]
    public async Task Only_the_official_repositories_are_asked_and_the_best_matching_sections_come_first_in_the_same_order_every_time()
    {
        var asked = new List<string>();
        var grounding = new Grounding(Serving(new() { ["unitreerobotics/unitree_rl_mjlab"] = MjlabReadme, ["unitreerobotics/unitree_sdk2"] = MjlabReadme }, asked), _cache);

        var first = await grounding.FindAsync("Training a walking policy with balance rewards", Ct);
        var second = await grounding.FindAsync("Training a walking policy with balance rewards", Ct);

        Assert.Equal(Grounding.OfficialRepos, asked.Take(Grounding.OfficialRepos.Count));
        Assert.Contains("unitreerobotics/unitree_rl_mjlab", asked); // where R1 training is actually supported
        Assert.StartsWith("## Training", first[0].Text);
        Assert.Equal("unitreerobotics/unitree_sdk2", first[0].Repo); // ties keep the fixed repository order
        Assert.DoesNotContain(first, s => s.Text.StartsWith("## License", StringComparison.Ordinal));
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task When_github_refuses_the_last_good_copy_is_used_and_with_none_saved_there_are_simply_no_snippets()
    {
        RepoSource rateLimited = (_, _, _) => throw new HttpRequestException("API rate limit exceeded");

        Assert.Empty(await new Grounding(rateLimited, _cache).FindAsync("R1 training", Ct));

        await new Grounding(Serving(new() { ["unitreerobotics/unitree_rl_mjlab"] = MjlabReadme }), _cache).FindAsync("R1 training", Ct);
        var snippets = await new Grounding(rateLimited, _cache).FindAsync("R1 training", Ct);

        Assert.NotEmpty(snippets);
        Assert.Equal("unitreerobotics/unitree_rl_mjlab", snippets[0].Repo);
    }

    [Fact]
    public void Figures_and_code_names_the_sources_do_not_contain_are_flagged_and_ones_they_do_contain_are_not()
    {
        string[] sources = ["The policy runs at 500Hz on the robot.", "Observations include joint_pos."];

        var claims = Grounding.UnverifiedClaims("I run my policy at 500Hz, read joint_pos and joint_vel, and move 23 joints.", sources);

        Assert.Equal(["joint_vel", "23"], claims);
    }

    /// <summary>Opt-in: real GitHub.</summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_github_returns_snippets_from_official_repositories()
    {
        if (Environment.GetEnvironmentVariable("POAUTOROBO_LIVE") != "1") return;
        var settings = await AppSettings.LoadAsync(new KeyVaultSecretSource(KeyVaultSecretSource.DefaultVault, AppSettings.SignedInUser), Ct);

        var snippets = await Grounding.Create(settings.GitHubToken, _cache).FindAsync("Training a walking policy for the R1 in MuJoCo", Ct);

        Assert.NotEmpty(snippets);
        Assert.All(snippets, s => Assert.Contains(s.Repo, Grounding.OfficialRepos));
        Assert.All(snippets, s => Assert.StartsWith("https://github.com/", s.Url));
    }
}
