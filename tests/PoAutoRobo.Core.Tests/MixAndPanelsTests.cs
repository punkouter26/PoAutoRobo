using System.Globalization;
using System.Text.RegularExpressions;
using NSubstitute;

namespace PoAutoRobo.Core.Tests;

public sealed class MixAndPanelsTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;
    private readonly Episode _episode = ProjectStoreTests.NewEpisode(10);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // ---- Mix ----

    [Fact]
    public void A_hand_picked_kind_survives_a_fresh_deal_and_only_clips_whose_kind_changes_drop_their_old_picture()
    {
        var withPictures = _episode with { Clips = [.. _episode.Clips.Select(c => c with { Visual = new VisualSpec(VisualKind.Still, MediaPaths: ["a.png"]) })] };
        var id = withPictures.Clips[3].Id;

        var picked = EpisodeEditor.SetKind(withPictures, id, VisualKind.AiVideo);
        var stillStills = Deal(picked with { MixSeed = 99 }, new MixPercentages(100, 0, 0));
        var titleCards = Deal(picked, new MixPercentages(0, 0, 100));

        Assert.Equal(new VisualSpec(VisualKind.AiVideo, KindLocked: true), stillStills.Clips[3].Visual);
        Assert.Equal(new VisualSpec(VisualKind.AiVideo, KindLocked: true), titleCards.Clips[3].Visual);
        Assert.All(stillStills.Clips.Where(c => c.Id != id), c => Assert.Equal(["a.png"], c.Visual.MediaPaths)); // same kind: picture kept
        Assert.All(titleCards.Clips.Where(c => c.Id != id), c => Assert.Equal(new VisualSpec(VisualKind.TitleCard), c.Visual));
        Assert.Equal(new MixPercentages(0, 0, 100), titleCards.Mix);
        // "My video" comes from attaching footage, never from picking a kind.
        Assert.Throws<ArgumentException>(() => EpisodeEditor.SetKind(_episode, id, VisualKind.UserVideo));
    }

    private static Episode Deal(Episode episode, MixPercentages mix) => VisualMix.Assign(episode with { Mix = mix }, mix);

    // ---- Panel sequences ----

    [Fact]
    public async Task A_panel_sequence_is_three_different_pictures_and_is_costed_as_three()
    {
        var images = Substitute.For<ImageMaker>();
        images.Invoke(default!, default!, default).ReturnsForAnyArgs(call =>
        {
            File.WriteAllBytes(call.ArgAt<string>(1), Guid.NewGuid().ToByteArray());
            return Task.CompletedTask;
        });
        var episode = EpisodeEditor.SetKind(_episode with { Clips = [_episode.Clips[0]] }, _episode.Clips[0].Id, VisualKind.MultiPanel);
        var visuals = new Visuals(images, new MediaCache(Path.Combine(_folder, "images")), Path.Combine(_folder, "none.png"), "gpt-image-1-mini");

        Assert.Equal(3, CostEstimate.For(episode, "gpt-image-1-mini").Pictures);
        var updated = await visuals.GenerateAsync(episode, episode.Clips[0].Id, Ct);

        Assert.Equal(3, updated.Clips[0].Visual.MediaPaths!.Distinct().Count());
        await images.ReceivedWithAnyArgs(3).Invoke(default!, default!, default);
        Assert.True(CostEstimate.For(updated, "gpt-image-1-mini").NothingToDo);
    }

    [Fact]
    public Task Panels_are_cut_evenly_across_the_narration() =>
        Verify(string.Join('\n', FfmpegArgs.PanelsVideo(["p1.png", "p2.png", "p3.png"], TimeSpan.FromSeconds(20.35), ExportPreset.Hd30, "clip_00.mp4")));

    [Fact]
    public void Panel_frame_counts_add_up_to_the_clip_length_exactly()
    {
        Panel_frame_counts_add_up_to_the_clip_length_exactlyCase(31.7, 4);
    }

    private void Panel_frame_counts_add_up_to_the_clip_length_exactlyCase(double seconds, int panels)
    {
        var args = string.Join(' ', FfmpegArgs.PanelsVideo([.. Enumerable.Range(0, panels).Select(i => $"p{i}.png")], TimeSpan.FromSeconds(seconds), ExportPreset.Hd30, "o.mp4"));

        var frames = Regex.Matches(args, @":d=(\d+):").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(panels, frames.Count);
        Assert.Equal((int)Math.Round(seconds * 30), frames.Sum());
        Assert.InRange(frames.Max() - frames.Min(), 0, 1);
    }

    [FfmpegFact]
    [Trait("Category", "Integration")] // runs FFmpeg for real
    public async Task A_panel_sequence_renders_to_the_right_length()
    {
        var ffmpeg = new FfmpegRunner(FfmpegRunner.Locate()!);
        var panels = new List<string>();
        foreach (var colour in new[] { "red", "green", "blue" })
        {
            panels.Add(Path.Combine(_folder, colour + ".png"));
            await ffmpeg.RunAsync(["-y", "-f", "lavfi", "-i", $"color=c={colour}:s=800x450", "-frames:v", "1", panels[^1]], _folder, null, null, Ct);
        }
        var output = Path.Combine(_folder, "panels.mp4");

        await ffmpeg.RunAsync(FfmpegArgs.PanelsVideo(panels, TimeSpan.FromSeconds(3), new ExportPreset(640, 360, 30), output), _folder, null, null, Ct);

        Assert.Equal(3.0, (await ffmpeg.ProbeDurationAsync(output, Ct)).TotalSeconds, precision: 1);
        var captioned = string.Join(' ', FfmpegArgs.PanelsVideo(panels, TimeSpan.FromSeconds(3), ExportPreset.Hd30, output, "captions_00.ass"));
        Assert.Contains("ass=captions_00.ass", captioned);
        Assert.Contains("fade=t=out:st=2.750", captioned);
    }

    // ---- Dealing the mix ----

    private static IEnumerable<VisualKind> Kinds(Episode e) => e.Clips.Select(c => c.Visual.Kind);

    /// <summary>The mix of plain picture types: how it is counted, checked, dealt, and what it leaves alone.</summary>
    [Fact]
    public void A_mix_is_counted_by_largest_remainder_must_add_up_deals_the_same_for_a_seed_and_leaves_chosen_clips_alone()
    {
        // still, panels, title, clips → expected counts
        Counts_use_largest_remainder_and_assignment_deals_exactly_those_countsCase(40, 40, 20, 4, 2, 1, 1); // 1.6, 1.6, 0.8: two spare seats, the largest remainder first and then the earlier of the tied pair
        Counts_use_largest_remainder_and_assignment_deals_exactly_those_countsCase(34, 33, 33, 16, 6, 5, 5); // 5.44, 5.28, 5.28: the one spare seat goes to the largest remainder
        Percentages_must_be_non_negative_and_sum_to_100();
        Same_seed_gives_the_same_assignment_and_a_different_seed_reshuffles();
        User_video_hand_picked_clips_and_the_kinds_the_script_chose_are_left_alone_and_excluded_from_the_counts();
    }

    private void Counts_use_largest_remainder_and_assignment_deals_exactly_those_countsCase(int still, int panels, int title, int clips, int eStill, int ePanels, int eTitle)
    {
        var mix = new MixPercentages(still, panels, title);
        var episode = ProjectStoreTests.NewEpisode(clips);

        var counts = VisualMix.Counts(mix, clips);
        var assigned = VisualMix.Assign(episode, mix);

        Assert.Equal((eStill, ePanels, eTitle), (counts[VisualKind.Still], counts[VisualKind.MultiPanel], counts[VisualKind.TitleCard]));
        Assert.All(counts, c => Assert.Equal(c.Value, Kinds(assigned).Count(k => k == c.Key)));
        Assert.Equal(episode.Clips.Select(c => c.Id), assigned.Clips.Select(c => c.Id)); // clips stay where they are
    }


    private void Percentages_must_be_non_negative_and_sum_to_100()
    {
        Assert.Throws<ArgumentException>(() => VisualMix.Counts(new MixPercentages(99, 0, 0), 16));
        Assert.Throws<ArgumentException>(() => VisualMix.Counts(new MixPercentages(110, -10, 0), 16));
    }

    private void Same_seed_gives_the_same_assignment_and_a_different_seed_reshuffles()
    {
        var episode = ProjectStoreTests.NewEpisode(20);
        var mix = new MixPercentages(34, 33, 33);

        Assert.Equal(Kinds(VisualMix.Assign(episode, mix)), Kinds(VisualMix.Assign(episode, mix)));
        Assert.NotEqual(Kinds(VisualMix.Assign(episode, mix)), Kinds(VisualMix.Assign(episode with { MixSeed = 7 }, mix)));
    }

    private void User_video_hand_picked_clips_and_the_kinds_the_script_chose_are_left_alone_and_excluded_from_the_counts()
    {
        var episode = ProjectStoreTests.NewEpisode(13);
        var clips = episode.Clips.ToList();
        clips[0] = clips[0] with { Visual = new VisualSpec(VisualKind.UserVideo, UserVideoPath: "lab.mp4") };
        clips[1] = clips[1] with { Visual = new VisualSpec(VisualKind.Still, KindLocked: true) };
        // Chosen by the script and already paid for: a re-deal of stills and title cards must not throw it away.
        clips[2] = clips[2] with { Visual = new VisualSpec(VisualKind.Animation, MediaPaths: ["scene.mp4"]) };
        episode = episode with { Clips = clips };

        var assigned = VisualMix.Assign(episode, new MixPercentages(50, 0, 50));

        Assert.Equal(clips[0].Visual, assigned.Clips[0].Visual);
        Assert.Equal(clips[1].Visual, assigned.Clips[1].Visual);
        Assert.Equal(clips[2].Visual, assigned.Clips[2].Visual);
        assigned = assigned with { Clips = [.. assigned.Clips.Skip(2)] }; // the hand-picked still is not one of the ten dealt
        Assert.Equal(5, Kinds(assigned).Count(k => k == VisualKind.Still)); // half of the ten that are left, not of all thirteen
        Assert.Equal(5, Kinds(assigned).Count(k => k == VisualKind.TitleCard));
    }
}
