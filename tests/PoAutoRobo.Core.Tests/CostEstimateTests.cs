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
    public void Only_clips_that_still_need_a_picture_are_counted()
    {
        var episode = With(
            new VisualSpec(VisualKind.Still),                                        // needs one
            new VisualSpec(VisualKind.Still, MediaPaths: ["done.png"]),              // has one
            new VisualSpec(VisualKind.Still, Stale: true, MediaPaths: ["old.png"]),  // out of date: needs one
            new VisualSpec(VisualKind.TitleCard),                                    // free
            new VisualSpec(VisualKind.UserVideo, UserVideoPath: "lab.mp4"));         // the user's own

        var estimate = CostEstimate.For(episode, "gpt-image-1-mini");

        Assert.Equal(2, estimate.Pictures);
        Assert.Equal([episode.Clips[0].Id, episode.Clips[2].Id], estimate.ClipIds);
    }

    [Fact]
    public void A_known_model_gets_a_dollar_figure_and_an_unknown_one_does_not_guess()
    {
        var episode = With(new VisualSpec(VisualKind.Still), new VisualSpec(VisualKind.Still));

        Assert.Equal(2 * CostEstimate.MiniPicturePrice, CostEstimate.For(episode, "gpt-image-1-mini").Dollars);
        Assert.Null(CostEstimate.For(episode, "some-future-model").Dollars);
    }

    [Fact]
    public void Summary_reads_as_a_plain_sentence_with_no_model_names()
    {
        var two = CostEstimate.For(With(new VisualSpec(VisualKind.Still), new VisualSpec(VisualKind.Still)), "gpt-image-1-mini");
        var unknown = CostEstimate.For(With(new VisualSpec(VisualKind.Still)), "some-future-model");
        var none = CostEstimate.For(With(new VisualSpec(VisualKind.TitleCard)), "gpt-image-1-mini");

        Assert.Equal("2 pictures, about $0.03.", two.Summary);
        Assert.Equal("1 picture. The cost depends on your Azure pricing.", unknown.Summary);
        Assert.Equal("Every clip already has its picture.", none.Summary);
        Assert.True(none.NothingToDo);
    }

    // ---- The host's character sheet ----

    private (Visuals Visuals, IImageGen Images, string Sheet) NewVisuals()
    {
        var images = Substitute.For<IImageGen>();
        images.GenerateAsync(default!, default!, default).ReturnsForAnyArgs(call =>
        {
            File.WriteAllBytes(call.ArgAt<string>(1), Guid.NewGuid().ToByteArray());
            return Task.CompletedTask;
        });
        var sheet = Path.Combine(_folder, "host", "sheet.png");
        return (new Visuals(images, new MediaCache(Path.Combine(_folder, "images")), sheet, "test-model"), images, sheet);
    }

    [Fact]
    public async Task Candidate_sheets_are_separate_pictures_made_from_the_written_description()
    {
        var (visuals, images, _) = NewVisuals();

        var candidates = await visuals.CandidateSheetsAsync(3, Ct);

        Assert.Equal(3, candidates.Distinct().Count());
        Assert.All(candidates, c => Assert.True(File.Exists(c)));
        await images.Received(3).GenerateAsync(Arg.Is<ImageRequest>(r => r.ReferencePath == null && r.Prompt.Contains("Unitree R1")), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Locking_a_sheet_makes_every_later_host_picture_use_it()
    {
        var (visuals, _, sheet) = NewVisuals();
        var chosen = (await visuals.CandidateSheetsAsync(2, Ct))[1];
        Assert.False(visuals.HasSheet);

        visuals.LockSheet(chosen);

        Assert.True(visuals.HasSheet);
        Assert.Equal(File.ReadAllBytes(chosen), File.ReadAllBytes(sheet));
        Assert.Equal(sheet, visuals.RequestFor(ProjectStoreTests.NewClip("x")).ReferencePath);
    }

    [Fact]
    public async Task Kinds_that_are_not_built_yet_get_a_single_still_for_now()
    {
        var (visuals, images, _) = NewVisuals();
        var episode = With(new VisualSpec(VisualKind.AiVideo));

        var updated = await visuals.GenerateAsync(episode, episode.Clips[0].Id, Ct);

        Assert.Single(updated.Clips[0].Visual.MediaPaths!);
        Assert.Equal(1, CostEstimate.For(episode, "gpt-image-1-mini").Pictures);
        await images.ReceivedWithAnyArgs(1).GenerateAsync(default!, default!, default);
    }
}
