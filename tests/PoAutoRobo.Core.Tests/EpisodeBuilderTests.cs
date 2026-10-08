using NSubstitute;

namespace PoAutoRobo.Core.Tests;

/// <summary>The parts of the builder that need no FFmpeg. Whole renders are in <see cref="EndToEndTests"/>.</summary>
public sealed class EpisodeBuilderTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Two_requests_for_the_same_narration_at_once_speak_it_only_once()
    {
        // Slow enough for the second request to arrive while the first is still speaking.
        var narrator = TestExtensions.SilentNarrator(takes: TimeSpan.FromMilliseconds(150));
        var builder = new EpisodeBuilder(narrator, new FfmpegRunner("ffmpeg.exe"));
        var clip = ProjectStoreTests.NewClip("x");

        var both = await Task.WhenAll(builder.NarrateClipAsync(clip, _folder, Ct), builder.NarrateClipAsync(clip, _folder, Ct));

        await narrator.ReceivedWithAnyArgs(1).SynthesizeAsync(default!, default!, default, default);
        Assert.Equal(both[0].AudioPath, both[1].AudioPath);
    }

    [Fact]
    public async Task Unused_media_is_what_no_clip_points_at_any_more_and_current_narration_and_pictures_are_kept()
    {
        var builder = new EpisodeBuilder(TestExtensions.SilentNarrator(), new FfmpegRunner("ffmpeg.exe"));
        var images = Directory.CreateDirectory(Path.Combine(_folder, "images")).FullName;
        var (current, replaced) = (Path.Combine(images, "current.png"), Path.Combine(images, "replaced.png"));
        File.WriteAllText(current, "");
        File.WriteAllText(replaced, "");
        var episode = ProjectStoreTests.NewEpisode(2);
        episode = EpisodeEditor.Update(episode, episode.Clips[0].Id, c => c with { Visual = new VisualSpec(VisualKind.Still, MediaPaths: [current]) });

        Assert.Null(builder.MeasuredDuration(episode.Clips[0], _folder)); // nothing recorded yet
        var firstTake = await builder.NarrateClipAsync(episode.Clips[0], _folder, Ct);
        await builder.NarrateClipAsync(episode.Clips[1], _folder, Ct);
        Assert.Equal(TimeSpan.FromSeconds(1), builder.MeasuredDuration(episode.Clips[0], _folder));
        Assert.Equal([replaced], builder.UnusedMedia(episode, _folder));

        // New wording: the clip is recorded again and the earlier take is left over.
        var edited = EpisodeEditor.ReplaceClip(episode, EpisodeEditor.WithDialogue(episode.Clips[0], "Different words now."));
        await builder.NarrateClipAsync(edited.Clips[0], _folder, Ct);

        Assert.Equal(
            new[] { firstTake.AudioPath, Path.ChangeExtension(firstTake.AudioPath, ".words.json"), replaced }.Order(),
            builder.UnusedMedia(edited, _folder).Order());
    }
}
