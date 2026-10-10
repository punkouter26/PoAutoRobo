using System.Net;
using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace PoAutoRobo.Core.Tests;

/// <summary>The picture types beyond a still: who chooses them, what each asks for, and how each is drawn.</summary>
public sealed class VisualKindsTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly AppSettings Live = new(new Uri("https://example.cognitiveservices.azure.com/"), null) { Credential = new FakeCredential(), VideoDeployment = "sora-2" };
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;
    private readonly ImageMaker _images = Substitute.For<ImageMaker>();
    private readonly List<ImageRequest> _asked = [];

    public VisualKindsTests() =>
        _images.Invoke(default!, default!, default).ReturnsForAnyArgs(call =>
        {
            _asked.Add(call.ArgAt<ImageRequest>(0));
            File.WriteAllBytes(call.ArgAt<string>(1), [9]);
            return Task.CompletedTask;
        });

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Clip Clip(VisualKind kind) => ProjectStoreTests.NewClip("Tower") with { Visual = new VisualSpec(kind) };

    private Visuals NewVisuals(Func<string, string, CancellationToken, Task>? maker = null, Func<SceneRequest, string, CancellationToken, Task>? scene = null) =>
        new(_images, new MediaCache(Path.Combine(_folder, "images")), Path.Combine(_folder, "sheet.png"), "test-model") { Stock = maker is null ? null : (words, _, output, ct) => maker(words, output, ct), Video = maker, Scene = scene };

    /// <summary>Answers every request through <paramref name="answer"/> and keeps the addresses asked for.</summary>
    private sealed class Web(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add($"{request.Method} {request.RequestUri!.AbsoluteUri}");
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

    // ---- Who chooses ----

    [Fact]
    public void The_scripts_choices_stand_except_kinds_that_cannot_be_made_and_ai_video_past_its_cap_and_a_script_with_none_is_dealt_the_default_mix()
    {
        var episode = ProjectStoreTests.NewEpisode(6);
        VisualKind[] chosen = [VisualKind.AiVideo, VisualKind.Stock, VisualKind.Animation, VisualKind.AiVideo, VisualKind.AiVideo, VisualKind.TitleCard];
        episode = episode with { Clips = [.. episode.Clips.Select((c, i) => c with { Visual = new VisualSpec(chosen[i]) })] };

        var settled = VisualMix.Settle(episode, kind => kind != VisualKind.Stock);

        Assert.Equal(
            [VisualKind.AiVideo, VisualKind.Still, VisualKind.Animation, VisualKind.AiVideo, VisualKind.Still, VisualKind.TitleCard],
            settled.Clips.Select(c => c.Visual.Kind));

        var unchosen = episode with { Clips = [.. episode.Clips.Select(c => c with { Visual = new VisualSpec(VisualKind.TitleCard) })] };
        Assert.Equal(VisualMix.Assign(unchosen, MixPercentages.Default), VisualMix.Settle(unchosen, _ => true), EpisodeComparer);
    }

    private static readonly IEqualityComparer<Episode> EpisodeComparer =
        EqualityComparer<Episode>.Create((a, b) => a!.Clips.Select(c => c.Visual).SequenceEqual(b!.Clips.Select(c => c.Visual)));

    [Fact]
    public async Task The_model_is_offered_only_the_kinds_that_can_be_made_its_choice_becomes_the_clips_kind_and_an_essay_has_no_host()
    {
        var calls = new List<(string System, string Schema)>();
        var writer = new AzureScriptWriter(AppSettings.Offline, (_, system, _, _, schema, _, _) =>
        {
            calls.Add((system, schema));
            return Task.FromResult("""{ "title": "Who checks the checker", "clips": [ { "title": "The hook", "kind": "chart", "b": { "dialogue": "d", "visualPrompt": "bar chart: a 1, b 2", "pose": "none" } } ] }""");
        })
        { CanMake = kind => kind != VisualKind.AiVideo };

        var essay = await writer.WriteEpisodeAsync("AI reviewing AI", Subject.Essay, [], EpisodeLength.QuickTest, Ct);
        var hosted = await writer.WriteEpisodeAsync("AI reviewing AI", Subject.General, [], EpisodeLength.QuickTest, Ct);

        Assert.Equal(VisualKind.Chart, essay.Clips[0].Visual.Kind);
        Assert.False(essay.Clips[0].HostVisible);
        Assert.True(hosted.Clips[0].HostVisible);
        Assert.Contains("\"chart\"", calls[0].Schema);
        Assert.DoesNotContain("\"video\"", calls[0].Schema);
        Assert.DoesNotContain("- video:", calls[0].System);
        Assert.Contains("hook", calls[0].System);              // an essay is one argument in order
        Assert.Contains("unseen voice", calls[0].System);
        Assert.Contains("reordered", calls[1].System);         // the hosted kind is still stand-alone clips
        Assert.Equal(VisualKind.TitleCard, Visuals.KindFor("no-such-kind"));
    }

    // ---- What each kind asks for ----

    [Fact]
    public async Task Stock_video_and_scenes_go_to_their_own_makers_once_and_say_what_is_missing_when_there_is_none()
    {
        var made = new List<string>();
        Task Make(string what, string path, CancellationToken _)
        {
            made.Add(what);
            File.WriteAllBytes(path, [1]);
            return Task.CompletedTask;
        }
        SceneRequest? scene = null;
        var visuals = NewVisuals(Make, (request, path, _) =>
        {
            scene = request;
            File.WriteAllBytes(path, [1]);
            return Task.CompletedTask;
        });

        var photo = await visuals.DrawAsync(Clip(VisualKind.Stock), Look.Comic, Ct);
        await visuals.DrawAsync(Clip(VisualKind.Stock), Look.Comic, Ct); // found again, not fetched again
        var video = await visuals.DrawAsync(Clip(VisualKind.AiVideo), Look.Cinematic, Ct);
        var chart = await visuals.DrawAsync(Clip(VisualKind.Chart), Look.FlatVector, Ct);

        Assert.EndsWith(".jpg", Assert.Single(photo));
        Assert.EndsWith(".mp4", Assert.Single(video));
        Assert.EndsWith(".mp4", Assert.Single(chart));
        Assert.Equal(2, made.Count);
        Assert.Equal("Tower prompt B", made[0]);                 // a stock search is the words alone
        Assert.Contains("dark and cinematic", made[1]);           // footage is described in the episode's look
        Assert.Equal(new SceneRequest(VisualKind.Chart, "Tower prompt B", Look.FlatVector, "Tower dialogue B", TimeSpan.FromSeconds(2)), scene);
        Assert.Empty(_asked);                                     // none of these is a picture from the picture model

        var bare = NewVisuals();
        Assert.False(bare.CanMake(VisualKind.Stock) || bare.CanMake(VisualKind.AiVideo) || bare.CanMake(VisualKind.Animation));
        Assert.True(bare.CanMake(VisualKind.Still) && bare.CanMake(VisualKind.Parallax));
        Assert.Contains("Pexels", (await Assert.ThrowsAsync<InvalidOperationException>(() => bare.DrawAsync(Clip(VisualKind.Stock), Look.Comic, Ct))).Message);
        Assert.Contains("POAUTOROBO_VIDEO_MODEL", (await Assert.ThrowsAsync<InvalidOperationException>(() => bare.DrawAsync(Clip(VisualKind.AiVideo), Look.Comic, Ct))).Message);
        Assert.Contains("Edge", (await Assert.ThrowsAsync<InvalidOperationException>(() => bare.DrawAsync(Clip(VisualKind.KineticText), Look.Comic, Ct))).Message);
    }

    [Fact]
    public async Task A_layered_picture_is_a_setting_and_a_cut_out_subject_and_the_look_changes_what_a_still_asks_for()
    {
        var visuals = NewVisuals();

        var layers = await visuals.DrawAsync(Clip(VisualKind.Parallax), Look.Photoreal, Ct);
        await visuals.DrawAsync(Clip(VisualKind.Still), Look.Comic, Ct);

        Assert.Equal(2, layers.Distinct().Count());
        Assert.False(_asked[0].Transparent);
        Assert.True(_asked[1].Transparent);
        Assert.All(_asked.Take(2), r => Assert.StartsWith(Visuals.StyleOf(Look.Photoreal), r.Prompt));
        Assert.StartsWith(Visuals.Style, _asked[2].Prompt);
        Assert.Equal(2, Visuals.PictureCount(VisualKind.Parallax));

        // Changing the look marks what was drawn in the old one, and only that.
        var episode = ProjectStoreTests.NewEpisode(2);
        episode = EpisodeEditor.Update(episode, episode.Clips[0].Id, c => c with { Visual = c.Visual with { MediaPaths = ["a.png"] } });
        var relit = EpisodeEditor.SetLook(episode, Look.Cinematic);
        Assert.Equal([true, false], relit.Clips.Select(c => c.Visual.Stale));
        Assert.Same(episode, EpisodeEditor.SetLook(episode, Look.Comic));
        Assert.All(EpisodeEditor.SetHostEverywhere(episode, false).Clips, c => Assert.False(c.HostVisible));
    }

    [Fact]
    public void An_estimate_prices_ai_video_and_counts_free_kinds_as_work_but_not_as_pictures()
    {
        var episode = ProjectStoreTests.NewEpisode(4);
        VisualKind[] kinds = [VisualKind.AiVideo, VisualKind.Animation, VisualKind.Stock, VisualKind.Parallax];
        episode = episode with { Clips = [.. episode.Clips.Select((c, i) => c with { Visual = new VisualSpec(kinds[i]) })] };

        var all = CostEstimate.For(episode, "gpt-image-1-mini");
        var free = CostEstimate.For(episode with { Clips = [episode.Clips[1], episode.Clips[2]] }, "some-future-model");

        Assert.Equal((4, 2, 1), (all.ClipIds.Count, all.Pictures, all.Videos));
        Assert.Equal(2 * CostEstimate.MiniPicturePrice + CostEstimate.VideoPrice, all.Dollars);
        Assert.Equal("2 pictures and 1 AI video, about $0.83.", all.Summary);
        Assert.False(free.NothingToDo);
        Assert.Equal("Animations and stock photos only, which cost no picture.", free.Summary);
    }

    // ---- The services ----

    [Fact]
    public async Task A_stock_photo_is_the_best_match_fetched_only_from_the_librarys_own_servers()
    {
        var web = new Web(request => request.RequestUri!.Host == "api.pexels.com"
            ? Json("""{ "photos": [ { "src": { "large2x": "https://images.pexels.com/photos/1/tower.jpeg?w=940" } } ] }""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([4, 2]) });
        var output = Path.Combine(_folder, "photo.jpg");

        await new PexelsStock("pexels-key", new HttpClient(web)).FindAsync("stone tower", 0, output, Ct);

        Assert.Equal([4, 2], File.ReadAllBytes(output));
        Assert.Contains("query=stone%20tower", web.Asked[0]);
        Assert.StartsWith("GET https://images.pexels.com/", web.Asked[1]);

        var elsewhere = new Web(_ => Json("""{ "photos": [ { "src": { "large2x": "https://example.com/tower.jpeg" } } ] }"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PexelsStock("k", new HttpClient(elsewhere)).FindAsync("tower", 0, output, Ct));
        Assert.Single(elsewhere.Asked); // the foreign address was never followed

        var none = new Web(_ => Json("""{ "photos": [] }"""));
        Assert.Contains("stone tower", (await Assert.ThrowsAsync<InvalidOperationException>(() => new PexelsStock("k", new HttpClient(none)).FindAsync("stone tower", 0, output, Ct))).Message);
    }

    [Fact]
    public async Task An_ai_video_is_asked_for_waited_on_and_downloaded_and_a_failure_is_reported_in_the_services_words()
    {
        var statuses = new Queue<string>(["queued", "in_progress", "completed"]);
        var web = new Web(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([6, 6, 6]) }
            : Json($$"""{ "id": "video_1", "status": "{{statuses.Dequeue()}}" }"""));
        var waits = 0;
        var output = Path.Combine(_folder, "shot.mp4");

        await new AzureVideoGen(Live, new HttpClient(web), Path.Combine(_folder, "jobs")) { Delay = (_, _) => { waits++; return Task.CompletedTask; } }.GenerateAsync("a tower rising", output, Ct);

        Assert.Equal([6, 6, 6], File.ReadAllBytes(output));
        Assert.Equal(2, waits);
        Assert.Equal(
            ["POST https://example.cognitiveservices.azure.com/openai/v1/videos", "GET https://example.cognitiveservices.azure.com/openai/v1/videos/video_1",
             "GET https://example.cognitiveservices.azure.com/openai/v1/videos/video_1", "GET https://example.cognitiveservices.azure.com/openai/v1/videos/video_1/content"],
            web.Asked);

        var refused = new Web(_ => Json("""{ "id": "video_2", "status": "failed", "error": { "message": "Blocked by the content filter." } }"""));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new AzureVideoGen(Live, new HttpClient(refused), Path.Combine(_folder, "jobs")).GenerateAsync("x", output, Ct));
        Assert.Contains("content filter", error.Message);

        var missing = new Web(_ => Json("""{ "error": { "message": "The API deployment for this resource does not exist." } }""", HttpStatusCode.NotFound));
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => new AzureVideoGen(Live, new HttpClient(missing), Path.Combine(_folder, "jobs")).GenerateAsync("x", output, Ct));
        Assert.Contains("does not exist", error.Message);
    }

    // ---- Drawing ----

    [Fact]
    public void Short_footage_repeats_stills_get_one_of_four_camera_moves_and_a_layered_picture_slides_its_subject_over_its_setting()
    {
        var length = TimeSpan.FromSeconds(20);
        string Still(int move) => string.Join(' ', FfmpegArgs.ClipVideo(ClipSource.Image, "p.png", length, ExportPreset.Hd30, "o.mp4", move: move));

        Assert.Contains("-stream_loop -1 -i shot.mp4", string.Join(' ', FfmpegArgs.ClipVideo(ClipSource.Loop, "shot.mp4", length, ExportPreset.Hd30, "o.mp4")));
        Assert.DoesNotContain("-stream_loop", string.Join(' ', FfmpegArgs.ClipVideo(ClipSource.Video, "lab.mp4", length, ExportPreset.Hd30, "o.mp4")));
        Assert.Equal(4, Enumerable.Range(0, 4).Select(Still).Distinct().Count());
        Assert.Equal(Still(0), string.Join(' ', FfmpegArgs.ClipVideo(ClipSource.Image, "p.png", length, ExportPreset.Hd30, "o.mp4"))); // the push-in is the default

        var layered = string.Join(' ', FfmpegArgs.ParallaxVideo("bg.png", "fg.png", length, ExportPreset.Hd30, "o.mp4", "captions.ass"));
        Assert.Contains("-i bg.png -i fg.png", layered);
        Assert.Contains("overlay=x='-(w-W)*t/20.000'", layered);
        Assert.Contains("ass=captions.ass", layered);
        Assert.Contains("fade=t=out:st=19.750", layered);
    }

    private static FfmpegRunner Ffmpeg => new(FfmpegRunner.Locate()!);

    [FfmpegFact]
    [Trait("Category", "Integration")]
    public async Task Layered_looping_and_drifting_clips_all_render_and_join_into_one_video()
    {
        var (setting, subject, shot) = (Path.Combine(_folder, "bg.png"), Path.Combine(_folder, "fg.png"), Path.Combine(_folder, "shot.mp4"));
        await Ffmpeg.RunAsync(["-y", "-f", "lavfi", "-i", "testsrc=s=800x600", "-frames:v", "1", setting], _folder, null, null, default);
        await Ffmpeg.RunAsync(["-y", "-f", "lavfi", "-i", "color=c=red@0.0:s=600x400,format=rgba", "-frames:v", "1", subject], _folder, null, null, default);
        await Ffmpeg.RunAsync(["-y", "-f", "lavfi", "-i", "testsrc=s=320x240:r=25:d=0.4", "-pix_fmt", "yuv420p", shot], _folder, null, null, default);
        var mock = await new MockScriptWriter().WriteEpisodeAsync("Layers", Subject.Essay, [], new EpisodeLength(3, 3), default);
        Clip Short(int i, VisualSpec visual) => mock.Clips[i] with
        {
            Visual = visual,
            Scripts = mock.Clips[i].Scripts.ToDictionary(s => s.Key, s => s.Value with { Dialogue = "Eight short words to say in this clip." }),
        };
        var episode = mock with
        {
            Clips =
            [
                Short(0, new VisualSpec(VisualKind.Parallax, MediaPaths: [setting, subject])),
                Short(1, new VisualSpec(VisualKind.AiVideo, MediaPaths: [shot])), // shorter than its narration, so it must repeat
                Short(2, new VisualSpec(VisualKind.Stock, MediaPaths: [setting])),
            ],
        };
        var builder = new EpisodeBuilder(new MockNarrator(), Ffmpeg) { ClipCacheFolder = Path.Combine(_folder, "clips") };

        var output = await builder.ExportAsync(episode, _folder, new ExportPreset(640, 360, 30), new CaptionStyle(), null, default);

        var narration = await builder.NarrateAsync(episode, _folder, default);
        var expected = FfmpegArgs.Timeline([.. narration.Select(n => n.Duration)])[^1] is var last ? last.Start + last.VideoLength : default;
        Assert.InRange((await Ffmpeg.ProbeDurationAsync(output, default)).TotalSeconds, expected.TotalSeconds - 0.2, expected.TotalSeconds + 0.2);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_folder, "clips")).Length);
        // Footage in a clip's media stands for the clip by its frame, and tidying up keeps both.
        var poster = await builder.PosterAsync(shot, default);
        Assert.Equal(poster, episode.Clips[1].Picture());
        Assert.DoesNotContain(poster, builder.UnusedMedia(episode, _folder));
    }

    [FfmpegFact]
    [Trait("Category", "Integration")] // runs Microsoft Edge and FFmpeg for real
    public async Task A_code_drawn_scene_is_rendered_by_edge_to_a_video_of_the_length_asked_and_broken_code_is_reported_plainly()
    {
        if (SceneRenderer.Locate() is not { } edge) return; // no Edge on this computer
        var renderer = new SceneRenderer(edge, Ffmpeg);
        var request = new SceneRequest(VisualKind.KineticText, "Who checks the checker", Look.Cinematic, "narration", TimeSpan.FromSeconds(2));
        var output = Path.Combine(_folder, "scene.mp4");

        await renderer.RenderAsync(await new MockScriptWriter().WriteSceneAsync(request, Ct), request.Length, output, Ct);

        var probe = await Ffmpeg.ProbeAsync(["-v", "error", "-show_entries", "stream=codec_name,width,height,nb_frames", "-of", "default=nw=1", output], Ct);
        Assert.Contains("codec_name=h264", probe);
        Assert.Contains($"width={SceneRenderer.Width}", probe);
        Assert.Contains($"nb_frames={2 * SceneRenderer.Fps}", probe);
        if (Environment.GetEnvironmentVariable("POAUTOROBO_KEEP") is { Length: > 0 } keep)
            File.Copy(output, Path.Combine(keep, "scene.mp4"), true);

        // The page may run its own script and nothing else.
        Assert.Contains("default-src 'none'", SceneRenderer.Page(new Scene("<svg/>", "")));
        var broken = new Scene("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", "function render(t) { nothing.here = t; }");
        // Code at fault is told apart from a browser at fault, and says what went wrong, so it can be written again.
        var error = await Assert.ThrowsAsync<SceneCodeException>(() => renderer.RenderAsync(broken, request.Length, output, Ct));
        Assert.Contains("code failed", error.Message);
        Assert.Contains("nothing is not defined", error.Detail);
        var unparsed = new Scene("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", "function render(t) {");
        await Assert.ThrowsAsync<SceneCodeException>(() => renderer.RenderAsync(unparsed, request.Length, output, Ct));
        // Code that never finishes is stopped, not left to hang the job until the user gives up.
        var endless = new Scene("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", "function render(t) { while (true) {} }");
        await Assert.ThrowsAsync<SceneCodeException>(() => renderer.RenderAsync(endless, request.Length, output, Ct));
    }

    [Fact]
    public void A_saved_episode_keeps_its_new_kinds_and_its_look()
    {
        var episode = ProjectStoreTests.NewEpisode(1) with { Look = Look.Cinematic };
        episode = episode with { Clips = [episode.Clips[0] with { Visual = new VisualSpec(VisualKind.KineticText) }] };

        ProjectStore.Save(episode, _folder);
        var loaded = ProjectStore.Load(_folder);

        Assert.Equal(Look.Cinematic, loaded.Look);
        Assert.Equal(VisualKind.KineticText, loaded.Clips[0].Visual.Kind);
        Assert.Contains("\"KineticText\"", File.ReadAllText(Path.Combine(_folder, ProjectStore.FileName)));
        Assert.Equal(Look.Comic, JsonSerializer.Deserialize<Episode>("""{ "Title": "t", "Topic": "t", "MixSeed": 1, "Clips": [] }""", ProjectStore.JsonOptions)!.Look); // older files have none
    }

    // ---- Takes ----

    [Fact]
    public async Task Another_take_is_a_fresh_request_kept_beside_the_last_one_which_can_be_gone_back_to_free()
    {
        var visuals = NewVisuals();
        var clip = Clip(VisualKind.Still);
        var episode = ProjectStoreTests.NewEpisode(1) with { Clips = [clip] };

        var first = await visuals.DrawAsync(clip, Look.Comic, Ct);
        episode = EpisodeEditor.ApplyPicture(episode, clip, first);
        var again = EpisodeEditor.NextTake(episode.Clips[0]);
        var second = await visuals.DrawAsync(again, Look.Comic, Ct);
        await visuals.DrawAsync(again, Look.Comic, Ct); // the same take again is found, not made again
        episode = EpisodeEditor.ApplyPicture(episode, again, second);

        Assert.NotEqual(first, second);
        await _images.ReceivedWithAnyArgs(2).Invoke(default!, default!, default);
        var shown = episode.Clips[0];
        Assert.Equal((second, 1), (shown.Visual.MediaPaths, shown.Visual.Take));
        Assert.Equal([first], shown.Visual.EarlierTakes);
        // The take not in use still belongs to the clip: tidying up must not delete it.
        Assert.Contains(first[0], shown.AllMedia());

        var back = EpisodeEditor.UseEarlierTake(episode, clip.Id, 0).Clips[0];
        Assert.Equal(first, back.Visual.MediaPaths);
        Assert.Equal([second], back.Visual.EarlierTakes);
        Assert.Equal(2, EpisodeEditor.NextTake(back).Visual.Take); // a number is never used twice, so a new take is never an old file
        await _images.ReceivedWithAnyArgs(2).Invoke(default!, default!, default); // going back cost nothing

        // A stock photograph's next take is the next match down.
        var asked = new List<int>();
        var stock = new Visuals(_images, new MediaCache(Path.Combine(_folder, "stock")), Path.Combine(_folder, "sheet.png"), "test-model")
        {
            Stock = (_, take, output, ct) => { asked.Add(take); return File.WriteAllBytesAsync(output, [1], ct); },
        };
        await stock.DrawAsync(Clip(VisualKind.Stock), Look.Comic, Ct);
        await stock.DrawAsync(EpisodeEditor.NextTake(Clip(VisualKind.Stock)), Look.Comic, Ct);
        Assert.Equal([0, 1], asked);
    }

    [Fact]
    public async Task A_video_stopped_while_being_made_is_picked_up_again_and_not_paid_for_twice_and_a_busy_service_is_waited_out()
    {
        var jobs = Path.Combine(_folder, "jobs");
        var output = Path.Combine(_folder, "shot.mp4");
        var slow = new Web(request => Json($$"""{ "id": "video_7", "status": "{{(request.Method == HttpMethod.Post ? "queued" : "in_progress")}}" }"""));
        var stopped = new AzureVideoGen(Live, new HttpClient(slow), jobs) { Delay = (_, _) => Task.FromCanceled(new CancellationToken(true)) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.GenerateAsync("a tower rising", output, Ct));
        Assert.Single(Directory.GetFiles(jobs)); // the video is paid for, so its name is kept

        var busy = true;
        var later = new Web(request =>
        {
            if (busy)
            {
                busy = false;
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }
            return request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([7]) }
                : Json("""{ "id": "video_7", "status": "completed" }""");
        });
        var waits = 0;
        await new AzureVideoGen(Live, new HttpClient(later), jobs) { Delay = (_, _) => { waits++; return Task.CompletedTask; } }.GenerateAsync("a tower rising", output, Ct);

        Assert.Equal([7], File.ReadAllBytes(output));
        Assert.Equal(1, waits); // the one refusal was waited out
        Assert.DoesNotContain(later.Asked, asked => asked.StartsWith("POST", StringComparison.Ordinal)); // never asked for a second time
        Assert.EndsWith("/videos/video_7", later.Asked[0]);
        Assert.Empty(Directory.GetFiles(jobs)); // delivered, so there is nothing left to pick up

        // A job the service has forgotten is simply begun again.
        Directory.CreateDirectory(jobs);
        var gone = new Web(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/video_old", StringComparison.Ordinal) ? Json("{}", HttpStatusCode.NotFound)
            : request.RequestUri.AbsolutePath.EndsWith("/content", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([8]) }
            : Json("""{ "id": "video_new", "status": "completed" }"""));
        File.WriteAllText(Path.Combine(jobs, Files.TextHash($"{Live.VideoDeployment}|a bridge")[..32] + ".job"), "video_old");
        await new AzureVideoGen(Live, new HttpClient(gone), jobs).GenerateAsync("a bridge", output, Ct);
        Assert.Equal([8], File.ReadAllBytes(output));
    }

    [Fact]
    public async Task A_picture_the_service_will_not_draw_is_made_another_way_a_diagram_then_a_photograph_then_code_then_its_title()
    {
        var clip = Clip(VisualKind.Still);
        var episode = ProjectStoreTests.NewEpisode(1) with { Clips = [clip] };
        var rethought = new SaferPicture("A labelled diagram of the stages.", "stone tower");
        // The picture model draws diagrams and nothing else.
        var picky = Substitute.For<ImageMaker>();
        picky.Invoke(default!, default!, default).ReturnsForAnyArgs(call =>
        {
            if (!call.Arg<ImageRequest>().Prompt.Contains("diagram", StringComparison.Ordinal)) throw new PictureDeclinedException("violence: medium");
            File.WriteAllBytes(call.ArgAt<string>(1), [3]);
            return Task.CompletedTask;
        });
        Visuals With(ImageMaker images, bool stock = false, bool scenes = false, string cache = "a") => new(images, new MediaCache(Path.Combine(_folder, cache)), Path.Combine(_folder, "sheet.png"), "test-model")
        {
            Rethink = (_, _) => Task.FromResult(rethought),
            Stock = stock ? (_, _, output, ct) => File.WriteAllBytesAsync(output, [4], ct) : null,
            Scene = scenes ? (_, output, ct) => File.WriteAllBytesAsync(output, [5], ct) : null,
        };

        // First the same idea, described as a diagram, in the picture type the clip had.
        var (drawn, paths, change) = await With(picky).DrawOrSubstituteAsync(clip, Look.Comic, Ct);
        Assert.Equal((VisualKind.Still, rethought.Diagram), (drawn.Visual.Kind, drawn.Active.VisualPrompt));
        Assert.Contains("(violence: medium), so it was described again as a diagram", change);
        var after = EpisodeEditor.ApplySubstitute(episode, clip, drawn, paths).Clips[0];
        Assert.Equal((rethought.Diagram, paths, clip.Active.Dialogue), (after.Active.VisualPrompt, after.Visual.MediaPaths, after.Active.Dialogue));
        // A clip changed while its picture was being made keeps the change, and the substitute is dropped.
        var edited = EpisodeEditor.SetKind(episode, clip.Id, VisualKind.Chart);
        Assert.Equal(edited, EpisodeEditor.ApplySubstitute(edited, clip, drawn, paths));

        // With a picture model that draws nothing at all: a photograph, then a diagram drawn in code, then the title alone.
        var never = Substitute.For<ImageMaker>();
        never.Invoke(default!, default!, default).ThrowsAsyncForAnyArgs(new PictureDeclinedException(null));
        (drawn, _, change) = await With(never, stock: true, scenes: true, cache: "b").DrawOrSubstituteAsync(clip, Look.Comic, Ct);
        Assert.Equal((VisualKind.Stock, "stone tower"), (drawn.Visual.Kind, drawn.Active.VisualPrompt));
        (drawn, _, _) = await With(never, scenes: true, cache: "c").DrawOrSubstituteAsync(clip, Look.Comic, Ct);
        Assert.Equal((VisualKind.Animation, rethought.Diagram), (drawn.Visual.Kind, drawn.Active.VisualPrompt));
        (drawn, paths, change) = await With(never, cache: "d").DrawOrSubstituteAsync(clip, Look.Comic, Ct);
        Assert.Equal(VisualKind.TitleCard, drawn.Visual.Kind);
        Assert.Empty(paths);
        Assert.EndsWith("so it shows its title.", change);
        Assert.Null(EpisodeEditor.ApplySubstitute(episode, clip, drawn, paths).Clips[0].Visual.MediaPaths);

        // A fault is not a refusal: it is reported as it always was, and nothing is made in the picture's place.
        var broken = Substitute.For<ImageMaker>();
        broken.Invoke(default!, default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("The service answered 500."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => With(broken, stock: true, cache: "e").DrawOrSubstituteAsync(clip, Look.Comic, Ct));
    }
}
