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
    private int _made;

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

    private Visuals VisualsWith(IImageGen images, string model = "test-model") =>
        new(images, new MediaCache(Path.Combine(_folder, "images")), _sheet, model) { PictureMade = () => _made++ };

    private Visuals Visuals => VisualsWith(_images);

    private Guid First => _episode.Clips[0].Id;

    [Fact]
    public async Task Regenerating_an_unchanged_clip_makes_no_second_request_and_is_not_counted_as_a_picture_made()
    {
        var once = await Visuals.GenerateAsync(_episode, First, Ct);
        Assert.Equal(1, _made);

        var twice = await Visuals.GenerateAsync(once, First, Ct);

        await _images.ReceivedWithAnyArgs(1).GenerateAsync(default!, default!, default);
        Assert.Equal(1, _made); // a saved picture costs nothing, so it must not be added to the spend
        Assert.True(File.Exists(Assert.Single(once.Clips[0].Visual.MediaPaths!)));
        Assert.Equal(once.Clips[0].Visual.MediaPaths, twice.Clips[0].Visual.MediaPaths);
        Assert.Same(_episode.Clips[1], twice.Clips[1]);
    }

    [Fact]
    public async Task A_changed_prompt_a_changed_host_sheet_or_another_model_each_cost_a_new_request()
    {
        await Visuals.GenerateAsync(_episode, First, Ct);

        await Visuals.GenerateAsync(EpisodeEditor.SetTier(_episode, First, Tier.C), First, Ct);
        File.WriteAllBytes(_sheet, [4, 5, 6]);
        await Visuals.GenerateAsync(_episode, First, Ct);
        await VisualsWith(_images, "other-model").GenerateAsync(_episode, First, Ct);

        await _images.ReceivedWithAnyArgs(4).GenerateAsync(default!, default!, default);
        Assert.Equal(4, _made);
    }

    [Fact]
    public async Task Host_visible_clips_send_the_character_sheet_and_the_pose_and_off_screen_clips_send_no_sheet_and_ask_for_a_diagram()
    {
        await Visuals.GenerateAsync(_episode, First, Ct);
        await Visuals.GenerateAsync(EpisodeEditor.SetHostVisible(_episode, First, false), First, Ct);

        await _images.Received(1).GenerateAsync(
            Arg.Is<ImageRequest>(r => r.ReferencePath == _sheet && r.Prompt.Contains(_episode.Clips[0].Active.Pose) && r.Prompt.Contains(_episode.Clips[0].Active.VisualPrompt)),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _images.Received(1).GenerateAsync(
            Arg.Is<ImageRequest>(r => r.ReferencePath == null && r.Prompt.Contains("no characters", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_picture_leaves_the_clip_as_it_was_and_nothing_half_written_in_the_cache()
    {
        var refusing = Substitute.For<IImageGen>(); // a fresh one: reconfiguring the shared substitute would run its file-writing callback
        refusing.GenerateAsync(default!, default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("The picture was blocked by the content filter."));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => VisualsWith(refusing).GenerateAsync(_episode, First, Ct));

        Assert.Contains("content filter", error.Message);
        Assert.Empty(Directory.Exists(Path.Combine(_folder, "images")) ? Directory.GetFiles(Path.Combine(_folder, "images")) : []);
        Assert.Equal(0, _made);
    }

    // ---- The Azure request itself ----

    /// <summary>Answers each request with the next scripted reply (the last one repeats) and keeps what was sent.</summary>
    private sealed class Scripted(params (HttpStatusCode Status, string Body, string? RetryAfter)[] replies) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, string? RetryAfter)> _replies = new(replies);
        public int Requests { get; private set; }
        public HttpRequestMessage? Request { get; private set; }
        public string Sent { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Request = request;
            Sent = Encoding.Latin1.GetString(await request.Content!.ReadAsByteArrayAsync(ct));
            var (status, body, retryAfter) = _replies.Count > 1 ? _replies.Dequeue() : _replies.Peek();
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
        }
    }

    // Signed as a stand-in user: the real sign-in is never asked for a token in a test.
    private static readonly AppSettings Live = new(new Uri("https://example.cognitiveservices.azure.com/"), null) { Credential = new FakeCredential() };
    private static string Reply(byte[] png) => $$"""{ "data": [ { "b64_json": "{{Convert.ToBase64String(png)}}" } ] }""";
    private const string RateLimited = """{ "error": { "code": "429", "message": "Your requests to gpt-image-1-mini in East US 2 have exceeded the call rate limit for your current AIServices S0 pricing tier. Please retry after 2 seconds." } }""";

    private static AzureImageGen ImageGen(Scripted handler, List<TimeSpan>? waits = null) =>
        new(Live, new HttpClient(handler)) { Delay = (wait, _) => { waits?.Add(wait); return Task.CompletedTask; } };

    private string Output => Path.Combine(_folder, "out.png");

    [Fact]
    public async Task Requests_are_signed_as_the_user_and_go_to_generations_as_json_or_with_a_reference_to_edits_as_a_file()
    {
        var handler = new Scripted((HttpStatusCode.OK, Reply([7, 7, 7, 7]), null));

        await ImageGen(handler).GenerateAsync(new ImageRequest("A whiteboard of reward terms", null), Output, Ct);

        Assert.Equal([7, 7, 7, 7], File.ReadAllBytes(Output));
        Assert.Contains($"/openai/deployments/{Live.ImageDeployment}/images/generations", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal($"Bearer {FakeCredential.Token}", handler.Request.Headers.Authorization!.ToString());
        Assert.False(handler.Request.Headers.Contains("api-key")); // the app holds no resource key to send
        Assert.Contains("A whiteboard of reward terms", handler.Sent);
        Assert.Contains("1536x1024", handler.Sent);

        // With a reference, the edits endpoint gets the host's sheet as a file.
        handler = new Scripted((HttpStatusCode.OK, Reply([8]), null));

        await ImageGen(handler).GenerateAsync(new ImageRequest("Host pointing", _sheet), Output, Ct);

        Assert.EndsWith("/images/edits", handler.Request!.RequestUri!.AbsolutePath);
        Assert.StartsWith("multipart/form-data", handler.Request.Content!.Headers.ContentType!.ToString());
        Assert.Contains("name=image", handler.Sent.Replace("\"", ""));
        Assert.Contains("\u0001\u0002\u0003", handler.Sent); // the sheet's bytes
    }

    [Fact]
    public async Task A_service_refusal_is_reported_in_its_own_words_is_not_retried_and_writes_no_file()
    {
        var handler = new Scripted((HttpStatusCode.BadRequest, """{ "error": { "code": "content_policy_violation", "message": "Your request was rejected by the safety system." } }""", null));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ImageGen(handler).GenerateAsync(new ImageRequest("x", null), Output, Ct));

        Assert.Contains("rejected by the safety system", error.Message);
        Assert.DoesNotContain(FakeCredential.Token, error.Message);
        Assert.Equal(1, handler.Requests);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task A_rate_limit_is_waited_out_for_as_long_as_the_service_asks_and_one_that_never_clears_gives_up_in_plain_words()
    {
        var handler = new Scripted((HttpStatusCode.TooManyRequests, RateLimited, "7"), (HttpStatusCode.TooManyRequests, RateLimited, null), (HttpStatusCode.OK, Reply([5, 5]), null));
        var waits = new List<TimeSpan>();

        await ImageGen(handler, waits).GenerateAsync(new ImageRequest("Host pointing", _sheet), Output, Ct);

        Assert.Equal([5, 5], File.ReadAllBytes(Output));
        Assert.Equal(3, handler.Requests);
        Assert.Equal(TimeSpan.FromSeconds(7), waits[0]);         // what the service asked for
        Assert.True(waits[1] >= TimeSpan.FromSeconds(5));        // a sensible wait when it does not say

        handler = new Scripted((HttpStatusCode.TooManyRequests, RateLimited, "1"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ImageGen(handler).GenerateAsync(new ImageRequest("x", null), Output, Ct));

        Assert.Equal(AzureImageGen.MaxAttempts, handler.Requests);
        Assert.Contains("busy", error.Message);
        Assert.DoesNotContain("gpt-image", error.Message);
        Assert.DoesNotContain("S0", error.Message);
    }

    /// <summary>Opt-in: one real low-cost picture, then a second using the first as its reference.</summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_pictures_come_back_with_and_without_a_reference()
    {
        if (Environment.GetEnvironmentVariable("POAUTOROBO_LIVE") != "1") return;
        var settings = await AppSettings.LoadAsync(new KeyVaultSecretSource(KeyVaultSecretSource.DefaultVault, AppSettings.SignedInUser), Ct);
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
