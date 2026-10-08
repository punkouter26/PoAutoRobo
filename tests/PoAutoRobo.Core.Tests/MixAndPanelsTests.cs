using System.Globalization;
using System.Text.RegularExpressions;
using NSubstitute;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

public sealed class MixAndPanelsTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;
    private readonly Episode _episode = ProjectStoreTests.NewEpisode(10);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Dictionary<VisualKind, int> Kinds(Episode e) => e.Clips.GroupBy(c => c.Visual.Kind).ToDictionary(g => g.Key, g => g.Count());

    // ---- Mix ----

    [Fact]
    public void Setting_the_mix_assigns_kinds_and_is_saved_with_the_episode()
    {
        var mix = new MixPercentages(40, 30, 0, 30);

        var mixed = EpisodeEditor.SetMix(_episode, mix);
        ProjectStore.Save(mixed, _folder);

        Assert.Equal(new Dictionary<VisualKind, int> { [VisualKind.Still] = 4, [VisualKind.MultiPanel] = 3, [VisualKind.TitleCard] = 3 }, Kinds(mixed));
        Assert.Equal(mix, ProjectStore.Load(_folder).Mix);
        Assert.Equal(MixPercentages.Default, _episode.Mix);
    }

    [Fact]
    public void Re_rolling_keeps_the_counts_but_deals_them_differently()
    {
        var mixed = EpisodeEditor.SetMix(_episode, new MixPercentages(50, 50, 0, 0));

        var rerolled = EpisodeEditor.RerollMix(mixed, newSeed: mixed.MixSeed + 1);

        Assert.Equal(Kinds(mixed), Kinds(rerolled));
        Assert.NotEqual(mixed.Clips.Select(c => c.Visual.Kind), rerolled.Clips.Select(c => c.Visual.Kind));
    }

    [Fact]
    public void A_hand_picked_kind_survives_mix_changes_and_re_rolls()
    {
        var id = _episode.Clips[3].Id;

        var picked = EpisodeEditor.SetKind(_episode, id, VisualKind.AiVideo);
        var later = EpisodeEditor.RerollMix(EpisodeEditor.SetMix(picked, new MixPercentages(100, 0, 0, 0)), newSeed: 99);

        Assert.Equal(VisualKind.AiVideo, later.Clips[3].Visual.Kind);
        Assert.True(later.Clips[3].Visual.KindLocked);
        Assert.Equal(9, Kinds(later)[VisualKind.Still]);
    }

    [Fact]
    public void A_clip_whose_kind_changes_drops_its_old_picture_and_one_that_keeps_its_kind_keeps_it()
    {
        var withPictures = _episode with { Clips = [.. _episode.Clips.Select(c => c with { Visual = new VisualSpec(VisualKind.Still, MediaPaths: ["a.png"]) })] };

        var allTitleCards = EpisodeEditor.SetMix(withPictures, new MixPercentages(0, 0, 0, 100));
        var stillStills = EpisodeEditor.SetMix(withPictures, new MixPercentages(100, 0, 0, 0));

        Assert.All(allTitleCards.Clips, c => Assert.Null(c.Visual.MediaPaths));
        Assert.All(stillStills.Clips, c => Assert.Equal(["a.png"], c.Visual.MediaPaths));
    }

    [Fact]
    public void Kind_cannot_be_set_to_my_video_by_hand()
    {
        Assert.Throws<ArgumentException>(() => EpisodeEditor.SetKind(_episode, _episode.Clips[0].Id, VisualKind.UserVideo));
    }

    // ---- Panel sequences ----

    [Fact]
    public async Task A_panel_sequence_is_three_different_pictures_and_is_costed_as_three()
    {
        var images = Substitute.For<IImageGen>();
        images.GenerateAsync(default!, default!, default).ReturnsForAnyArgs(call =>
        {
            File.WriteAllBytes(call.ArgAt<string>(1), Guid.NewGuid().ToByteArray());
            return Task.CompletedTask;
        });
        var episode = EpisodeEditor.SetKind(_episode with { Clips = [_episode.Clips[0]] }, _episode.Clips[0].Id, VisualKind.MultiPanel);
        var visuals = new Visuals(images, new MediaCache(Path.Combine(_folder, "images")), Path.Combine(_folder, "none.png"), "gpt-image-1-mini");

        Assert.Equal(3, CostEstimate.For(episode, "gpt-image-1-mini").Pictures);
        var updated = await visuals.GenerateAsync(episode, episode.Clips[0].Id, Ct);

        Assert.Equal(3, updated.Clips[0].Visual.MediaPaths!.Distinct().Count());
        await images.ReceivedWithAnyArgs(3).GenerateAsync(default!, default!, default);
        Assert.True(CostEstimate.For(updated, "gpt-image-1-mini").NothingToDo);
    }

    [Fact]
    public Task Panels_are_cut_evenly_across_the_narration() =>
        Verify(string.Join('\n', FfmpegArgs.PanelsVideo(["p1.png", "p2.png", "p3.png"], TimeSpan.FromSeconds(20.35), ExportPreset.Hd30, "clip_00.mp4")));

    [Theory]
    [InlineData(20.35, 3)]
    [InlineData(15.0, 2)]
    [InlineData(31.7, 4)]
    public void Panel_frame_counts_add_up_to_the_clip_length_exactly(double seconds, int panels)
    {
        var args = string.Join(' ', FfmpegArgs.PanelsVideo([.. Enumerable.Range(0, panels).Select(i => $"p{i}.png")], TimeSpan.FromSeconds(seconds), ExportPreset.Hd30, "o.mp4"));

        var frames = Regex.Matches(args, @":d=(\d+):").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(panels, frames.Count);
        Assert.Equal((int)Math.Round(seconds * 30), frames.Sum());
        Assert.InRange(frames.Max() - frames.Min(), 0, panels);
    }

    [FfmpegFact]
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
    }
}
