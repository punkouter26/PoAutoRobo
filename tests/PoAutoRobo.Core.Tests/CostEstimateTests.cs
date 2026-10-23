using NSubstitute;

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
    public async Task Pictures_are_costed_before_they_are_made_and_the_host_and_logo_are_chosen_from_candidates()
    {
        Only_clips_that_still_need_a_picture_are_counted_and_priced_only_when_the_model_price_is_known();
        await Candidate_sheets_are_separate_pictures_made_from_the_written_description_and_locking_one_makes_host_pictures_and_logos_use_it();
    }

    private void Only_clips_that_still_need_a_picture_are_counted_and_priced_only_when_the_model_price_is_known()
    {
        var episode = With(
            new VisualSpec(VisualKind.Still),                                        // needs one
            new VisualSpec(VisualKind.Still, MediaPaths: ["done.png"]),              // has one
            new VisualSpec(VisualKind.Still, Stale: true, MediaPaths: ["old.png"]),  // out of date: needs one
            new VisualSpec(VisualKind.TitleCard),                                    // free
            new VisualSpec(VisualKind.UserVideo, UserVideoPath: "lab.mp4"),          // the user's own
            new VisualSpec(VisualKind.AiVideo));                                     // footage: no picture, and priced as a video

        var estimate = CostEstimate.For(episode, "gpt-image-1-mini");

        Assert.Equal(2, estimate.Pictures);
        Assert.Equal(1, estimate.Videos);
        Assert.Equal([episode.Clips[0].Id, episode.Clips[2].Id, episode.Clips[5].Id], estimate.ClipIds);
        Assert.Equal(2 * CostEstimate.MiniPicturePrice + CostEstimate.VideoPrice, estimate.Dollars);

        // A batch holds to the figure agreed: each clip's price adds up to it, and a clip that would pass it is not drawn.
        Assert.Equal(estimate.Dollars, estimate.ClipIds.Sum(id => CostEstimate.PriceOf(episode.Clips.First(c => c.Id == id), "gpt-image-1-mini")));
        Assert.True(estimate.Allows(2 * CostEstimate.MiniPicturePrice, CostEstimate.VideoPrice)); // the last clip lands exactly on it
        Assert.False(estimate.Allows(3 * CostEstimate.MiniPicturePrice, CostEstimate.VideoPrice)); // one picture more than planned was made

        // A known model gets a dollar figure, an unknown one does not guess, and the summary names no model.
        var two = CostEstimate.For(With(new VisualSpec(VisualKind.Still), new VisualSpec(VisualKind.Still)), "gpt-image-1-mini");
        var unknown = CostEstimate.For(With(new VisualSpec(VisualKind.Still)), "some-future-model");
        var none = CostEstimate.For(With(new VisualSpec(VisualKind.TitleCard)), "gpt-image-1-mini");

        Assert.Equal(2 * CostEstimate.PriceOf("gpt-image-1-mini"), two.Dollars);
        Assert.Null(unknown.Dollars);
        Assert.True(unknown.Allows(100, 1)); // no figure to hold to: only the monthly budget stops it
        Assert.Equal("2 pictures, about $0.03.", two.Summary);
        Assert.Equal("1 picture. The cost depends on your Azure pricing.", unknown.Summary);
        Assert.Equal("Every clip already has its picture.", none.Summary);
        Assert.True(none.NothingToDo);
    }

    // ---- The host's character sheet ----

    private async Task Candidate_sheets_are_separate_pictures_made_from_the_written_description_and_locking_one_makes_host_pictures_and_logos_use_it()
    {
        var images = Substitute.For<ImageMaker>();
        images.Invoke(default!, default!, default).ReturnsForAnyArgs(call =>
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
        await images.Received(3).Invoke(Arg.Is<ImageRequest>(r => r.ReferencePath == null && r.Prompt.Contains("Unitree R1")), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Until one is locked, the host is described in words and no reference is sent.
        Assert.False(visuals.HasSheet);
        Assert.Null(visuals.RequestFor(clip).ReferencePath);
        Assert.Contains(Visuals.HostInWords, visuals.RequestFor(clip).Prompt);

        visuals.LockSheet(candidates[1]);

        Assert.True(visuals.HasSheet);
        Assert.Equal(File.ReadAllBytes(candidates[1]), File.ReadAllBytes(sheet));
        Assert.Equal(sheet, visuals.RequestFor(clip).ReferencePath);

        // Logos are made the same way: separate takes, drawn from the locked host, square, on nothing, with no lettering.
        var logos = await visuals.CandidateLogosAsync("Teaching the R1", "#22CC88", 3, Ct);

        Assert.Equal(3, logos.Distinct().Count());
        await images.Received(3).Invoke(Arg.Is<ImageRequest>(r => r.ReferencePath == sheet && r.Transparent && r.Size == "1024x1024"
            && r.Prompt.Contains("Teaching the R1") && r.Prompt.Contains("#22CC88") && r.Prompt.Contains("No letters")), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- What has been spent ----

    [Fact]
    public void Spend_adds_up_across_entries_and_an_episode_with_no_log_has_spent_nothing()
    {
        var episode = Path.Combine(_folder, "episode"); // not created yet: the first entry makes it

        Assert.Equal(0m, SpendLog.Total(episode));

        SpendLog.Add(episode, "3 pictures", 0.045m);
        SpendLog.Add(episode, "1 picture", 0.015m);

        Assert.Equal(0.06m, SpendLog.Total(episode));
        Assert.Equal("$0.06", SpendLog.Money(SpendLog.Total(episode)));

        // Script and voice use has no price the app knows; it is counted, and adds nothing to the dollars.
        SpendLog.Add(episode, "script tokens", 0, units: 1200);
        SpendLog.Add(episode, "script tokens", 0, units: 300);
        SpendLog.Add(episode, "voice characters", 0, units: 90);
        Assert.Equal(1500, SpendLog.Units(episode, "script tokens"));
        Assert.Equal(90, SpendLog.Units(episode, "voice characters"));
        Assert.Equal(0.06m, SpendLog.Total(episode));

        // Priced script use counts towards the total, and the breakdown lists each kind once, dearest first.
        SpendLog.Add(episode, SpendLog.ScriptTokens, 0.10m, units: 500);
        Assert.Equal(0.16m, SpendLog.Total(episode));
        Assert.Equal(new SpendLine(SpendLog.ScriptTokens, 0.10m, 2000), SpendLog.Breakdown(episode)[0]);
        Assert.Equal(["script tokens", "3 pictures", "1 picture", "voice characters"], SpendLog.Breakdown(episode).Select(line => line.What));

        // The month's total covers every episode under the root, and only that month.
        SpendLog.Add(Path.Combine(_folder, "another"), SpendLog.AiVideo, 0.80m);
        Assert.Equal(0.96m, SpendLog.MonthTotal(_folder, DateTimeOffset.Now));
        Assert.Equal(0m, SpendLog.MonthTotal(_folder, DateTimeOffset.Now.AddMonths(2)));
        Assert.Equal(0m, SpendLog.MonthTotal(Path.Combine(_folder, "no-such-root"), DateTimeOffset.Now));
    }
}
