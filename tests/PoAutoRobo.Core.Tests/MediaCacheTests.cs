using System.Net;
using System.Text;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

public sealed class MediaCacheTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;
    private readonly IImageGen _images = Substitute.For<IImageGen>();
    private readonly string _sheet;
    private readonly Episode _episode;

    public MediaCacheTests()
    {
        _sheet = Path.Combine(_folder, "sheet.png");
        File.WriteAllBytes(_sheet, [1, 2, 3]);
        _images.GenerateAsync(default!, default!, default).ReturnsForAnyArgs(call =>
        {
            File.WriteAllBytes(call.ArgAt<string>(1), [9, 9, 9]);
            return Task.CompletedTask;
        });
        var episode = ProjectStoreTests.NewEpisode(2);
        _episode = episode with { Clips = [.. episode.Clips.Select(c => c with { Visual = new VisualSpec(VisualKind.Still) })] };
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private Visuals Visuals => new(_images, new MediaCache(Path.Combine(_folder, "images")), _sheet, "test-model");

    private Guid First => _episode.Clips[0].Id;

    [Fact]
    public async Task Generating_a_still_attaches_the_picture_and_clears_the_stale_flag()
    {
        var stale = _episode with { Clips = [_episode.Clips[0] with { Visual = new VisualSpec(VisualKind.Still, Stale: true) }, _episode.Clips[1]] };

        var updated = await Visuals.GenerateAsync(stale, First, Ct);

        var visual = updated.Clips[0].Visual;
        Assert.False(visual.Stale);
        Assert.True(File.Exists(Assert.Single(visual.MediaPaths!)));
        Assert.Same(stale.Clips[1], updated.Clips[1]);
    }

    [Fact]
    public async Task Regenerating_an_unchanged_clip_makes_no_second_request()
    {
        var once = await Visuals.GenerateAsync(_episode, First, Ct);
        var twice = await Visuals.GenerateAsync(once, First, Ct);

        await _images.ReceivedWithAnyArgs(1).GenerateAsync(default!, default!, default);
        Assert.Equal(once.Clips[0].Visual.MediaPaths, twice.Clips[0].Visual.MediaPaths);
    }

    [Fact]
    public async Task A_changed_prompt_a_changed_host_sheet_or_another_model_each_cost_a_new_request()
    {
        await Visuals.GenerateAsync(_episode, First, Ct);

        await Visuals.GenerateAsync(EpisodeEditor.SetTier(_episode, First, Tier.C), First, Ct);
        File.WriteAllBytes(_sheet, [4, 5, 6]);
        await Visuals.GenerateAsync(_episode, First, Ct);
        await new Visuals(_images, new MediaCache(Path.Combine(_folder, "images")), _sheet, "other-model").GenerateAsync(_episode, First, Ct);

        await _images.ReceivedWithAnyArgs(4).GenerateAsync(default!, default!, default);
    }

    [Fact]
    public async Task Host_visible_clips_send_the_character_sheet_and_describe_the_pose()
    {
        await Visuals.GenerateAsync(_episode, First, Ct);

        await _images.Received().GenerateAsync(
            Arg.Is<ImageRequest>(r => r.ReferencePath == _sheet && r.Prompt.Contains(_episode.Clips[0].Active.Pose) && r.Prompt.Contains(_episode.Clips[0].Active.VisualPrompt)),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Off_screen_clips_send_no_sheet_and_ask_for_a_diagram_without_the_host()
    {
        var offScreen = EpisodeEditor.SetHostVisible(_episode, First, false);

        await Visuals.GenerateAsync(offScreen, First, Ct);

        await _images.Received().GenerateAsync(
            Arg.Is<ImageRequest>(r => r.ReferencePath == null && r.Prompt.Contains("no characters", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Before_a_host_sheet_exists_the_host_is_described_in_words_instead()
    {
        File.Delete(_sheet);

        await Visuals.GenerateAsync(_episode, First, Ct);

        await _images.Received().GenerateAsync(
            Arg.Is<ImageRequest>(r => r.ReferencePath == null && r.Prompt.Contains("Unitree R1")), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_picture_leaves_the_clip_as_it_was_and_nothing_half_written_in_the_cache()
    {
        var refusing = Substitute.For<IImageGen>(); // a fresh one: reconfiguring the shared substitute would run its file-writing callback
        refusing.GenerateAsync(default!, default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("The picture was blocked by the content filter."));
        var visuals = new Visuals(refusing, new MediaCache(Path.Combine(_folder, "images")), _sheet, "test-model");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => visuals.GenerateAsync(_episode, First, Ct));

        Assert.Contains("content filter", error.Message);
        Assert.Empty(Directory.Exists(Path.Combine(_folder, "images")) ? Directory.GetFiles(Path.Combine(_folder, "images")) : []);
    }

    // ---- The Azure request itself ----

    private sealed class Recorder(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Sent { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Sent = Encoding.Latin1.GetString(await request.Content!.ReadAsByteArrayAsync(ct));
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static readonly AppSettings Live = new(new Uri("https://example.cognitiveservices.azure.com/"), "key-value", null);
    private static string Reply(byte[] png) => $$"""{ "data": [ { "b64_json": "{{Convert.ToBase64String(png)}}" } ] }""";

    [Fact]
    public async Task Without_a_reference_the_generations_endpoint_gets_json_and_the_picture_is_saved()
    {
        var recorder = new Recorder(HttpStatusCode.OK, Reply([7, 7, 7, 7]));
        var output = Path.Combine(_folder, "out.png");

        await new AzureImageGen(Live, new HttpClient(recorder)).GenerateAsync(new ImageRequest("A whiteboard of reward terms", null), output, Ct);

        Assert.Equal([7, 7, 7, 7], File.ReadAllBytes(output));
        Assert.Contains($"/openai/deployments/{Live.ImageDeployment}/images/generations", recorder.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("key-value", recorder.Request.Headers.GetValues("api-key").Single());
        Assert.Contains("A whiteboard of reward terms", recorder.Sent);
        Assert.Contains("1536x1024", recorder.Sent);
    }

    [Fact]
    public async Task With_a_reference_the_edits_endpoint_gets_the_sheet_as_a_file()
    {
        var recorder = new Recorder(HttpStatusCode.OK, Reply([8]));

        await new AzureImageGen(Live, new HttpClient(recorder)).GenerateAsync(new ImageRequest("Host pointing", _sheet), Path.Combine(_folder, "out.png"), Ct);

        Assert.EndsWith("/images/edits", recorder.Request!.RequestUri!.AbsolutePath);
        Assert.StartsWith("multipart/form-data", recorder.Request.Content!.Headers.ContentType!.ToString());
        Assert.Contains("name=image", recorder.Sent.Replace("\"", ""));
        Assert.Contains("\u0001\u0002\u0003", recorder.Sent); // the sheet's bytes
    }

    [Fact]
    public async Task A_service_refusal_is_reported_in_its_own_words_and_writes_no_file()
    {
        var recorder = new Recorder(HttpStatusCode.BadRequest, """{ "error": { "code": "content_policy_violation", "message": "Your request was rejected by the safety system." } }""");
        var output = Path.Combine(_folder, "out.png");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AzureImageGen(Live, new HttpClient(recorder)).GenerateAsync(new ImageRequest("x", null), output, Ct));

        Assert.Contains("rejected by the safety system", error.Message);
        Assert.DoesNotContain("key-value", error.Message);
        Assert.False(File.Exists(output));
    }

    /// <summary>Opt-in: one real low-cost picture, then a second using the first as its reference.</summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_pictures_come_back_with_and_without_a_reference()
    {
        if (Environment.GetEnvironmentVariable("POAUTOROBO_LIVE") != "1") return;
        var settings = await AppSettings.LoadAsync(new KeyVaultSecretSource(KeyVaultSecretSource.DefaultVault), Ct);
        var images = new AzureImageGen(settings, AzureImageGen.NewHttpClient()) { Quality = "low" };
        var plain = Path.Combine(_folder, "plain.png");
        var withReference = Path.Combine(_folder, "ref.png");

        await images.GenerateAsync(new ImageRequest("Comic panel: a friendly cartoon humanoid robot waving in a robotics lab.", null), plain, Ct);
        await images.GenerateAsync(new ImageRequest("Comic panel: the same robot pointing at a whiteboard.", plain), withReference, Ct);

        Assert.True(new FileInfo(plain).Length > 10_000);
        Assert.True(new FileInfo(withReference).Length > 10_000);
        if (Environment.GetEnvironmentVariable("POAUTOROBO_KEEP") is { Length: > 0 } keep)
        {
            File.Copy(plain, Path.Combine(keep, "live-plain.png"), true);
            File.Copy(withReference, Path.Combine(keep, "live-ref.png"), true);
        }
    }
}
