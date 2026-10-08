using NSubstitute;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

public sealed class CostEstimateTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Episode With(params VisualSpec[] visuals)
    {
        var episode = ProjectStoreTests.NewEpisode(visuals.Length);
        return episode with { Clips = [.. episode.Clips.Select((c, i) => c with { Visual = visuals[i] })] };
    }

    [Fact]
    public void Only_clips_that_still_need_a_picture_are_counted_and_priced_only_when_the_model_price_is_known()
    {
        var episode = With(
            new VisualSpec(VisualKind.Still),                                        // needs one
            new VisualSpec(VisualKind.Still, MediaPaths: ["done.png"]),              // has one
            new VisualSpec(VisualKind.Still, Stale: true, MediaPaths: ["old.png"]),  // out of date: needs one
            new VisualSpec(VisualKind.TitleCard),                                    // free
            new VisualSpec(VisualKind.UserVideo, UserVideoPath: "lab.mp4"),          // the user's own
            new VisualSpec(VisualKind.AiVideo));                                     // a single still until video is built

        var estimate = CostEstimate.For(episode, "gpt-image-1-mini");

        Assert.Equal(3, estimate.Pictures);
        Assert.Equal([episode.Clips[0].Id, episode.Clips[2].Id, episode.Clips[5].Id], estimate.ClipIds);
        Assert.Equal(3 * CostEstimate.MiniPicturePrice, estimate.Dollars);

        // A known model gets a dollar figure, an unknown one does not guess, and the summary names no model.
        var two = CostEstimate.For(With(new VisualSpec(VisualKind.Still), new VisualSpec(VisualKind.Still)), "gpt-image-1-mini");
        var unknown = CostEstimate.For(With(new VisualSpec(VisualKind.Still)), "some-future-model");
        var none = CostEstimate.For(With(new VisualSpec(VisualKind.TitleCard)), "gpt-image-1-mini");

        Assert.Equal(2 * CostEstimate.PriceOf("gpt-image-1-mini"), two.Dollars);
        Assert.Null(unknown.Dollars);
        Assert.Equal("2 pictures, about $0.03.", two.Summary);
        Assert.Equal("1 picture. The cost depends on your Azure pricing.", unknown.Summary);
        Assert.Equal("Every clip already has its picture.", none.Summary);
        Assert.True(none.NothingToDo);
    }

    // ---- The host's character sheet ----

    [Fact]
    public async Task Candidate_sheets_are_separate_pictures_made_from_the_written_description_and_locking_one_makes_host_pictures_use_it()
    {
        var images = Substitute.For<IImageGen>();
        images.GenerateAsync(default!, default!, default).ReturnsForAnyArgs(call =>
        {
            File.WriteAllBytes(call.ArgAt<string>(1), Guid.NewGuid().ToByteArray());
            return Task.CompletedTask;
        });
        var sheet = Path.Combine(_folder, "host", "sheet.png");
        var visuals = new Visuals(images, new MediaCache(Path.Combine(_folder, "images")), sheet, "test-model");
        var clip = ProjectStoreTests.NewClip("x");

        var candidates = await visuals.CandidateSheetsAsync(3, Ct);

        Assert.Equal(3, candidates.Distinct().Count());
        Assert.All(candidates, c => Assert.True(File.Exists(c)));
        await images.Received(3).GenerateAsync(Arg.Is<ImageRequest>(r => r.ReferencePath == null && r.Prompt.Contains("Unitree R1")), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Until one is locked, the host is described in words and no reference is sent.
        Assert.False(visuals.HasSheet);
        Assert.Null(visuals.RequestFor(clip).ReferencePath);
        Assert.Contains(Visuals.HostInWords, visuals.RequestFor(clip).Prompt);

        visuals.LockSheet(candidates[1]);

        Assert.True(visuals.HasSheet);
        Assert.Equal(File.ReadAllBytes(candidates[1]), File.ReadAllBytes(sheet));
        Assert.Equal(sheet, visuals.RequestFor(clip).ReferencePath);
    }
}
