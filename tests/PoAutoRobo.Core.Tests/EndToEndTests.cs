using System.Globalization;
using System.Text.RegularExpressions;

namespace PoAutoRobo.Core.Tests;

/// <summary>Runs only where FFmpeg is installed; skipped elsewhere.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (FfmpegRunner.Locate() is null)
            Skip = "FFmpeg is not installed.";
    }
}

[Trait("Category", "Integration")] // these run FFmpeg for real
public sealed class EndToEndTests : IDisposable
{
    private static readonly ExportPreset Small = new(640, 360, 30); // small frame keeps the test quick
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static FfmpegRunner Ffmpeg => new(FfmpegRunner.Locate()!);

    private static EpisodeBuilder Builder => new(new MockNarrator(), Ffmpeg);

    /// <summary>Mock script cut down to three short clips: a title card, a still image and user footage.</summary>
    private async Task<Episode> ThreeClipEpisodeAsync()
    {
        var image = Path.Combine(_folder, "panel.png");
        var video = Path.Combine(_folder, "lab.mp4");
        await Ffmpeg.RunAsync(["-y", "-f", "lavfi", "-i", "testsrc=s=800x600", "-frames:v", "1", image], _folder, null, null, default);
        await Ffmpeg.RunAsync(["-y", "-f", "lavfi", "-i", "testsrc=s=320x240:r=25:d=1", "-pix_fmt", "yuv420p", video], _folder, null, null, default);

        var episode = await new MockScriptWriter().WriteEpisodeAsync("Balancing the R1", Subject.UnitreeR1, [], EpisodeLength.Full, default);
        static Clip Short(Clip c, VisualSpec visual) => c with
        {
            Visual = visual,
            Scripts = c.Scripts.ToDictionary(s => s.Key, s => s.Value with { Dialogue = string.Join(' ', Durations.SplitWords(s.Value.Dialogue).Take(8)) + "." }),
        };
        return episode with
        {
            Clips =
            [
                Short(episode.Clips[0], new VisualSpec(VisualKind.TitleCard)),
                Short(episode.Clips[1], new VisualSpec(VisualKind.Still, MediaPaths: [image])),
                Short(episode.Clips[2], new VisualSpec(VisualKind.UserVideo, UserVideoPath: video)),
            ],
        };
    }

    [FfmpegFact]
    public async Task Mock_topic_exports_h264_aac_at_the_chosen_size_rate_and_loudness()
    {
        var episode = await ThreeClipEpisodeAsync();
        var progress = new List<RenderProgress>();

        // The user's footage has no picture of its own until a frame is taken from it; that frame then stands for the clip.
        var footage = episode.Clips[2];
        Assert.Null(footage.Picture());
        var poster = await Builder.PosterAsync(footage.Visual.UserVideoPath!, default);
        Assert.Equal(poster, footage.Picture());
        Assert.True(new FileInfo(poster).Length > 0);
        Assert.DoesNotContain(poster, Builder.UnusedMedia(episode, _folder)); // tidying up never takes it away

        var output = await Builder.ExportAsync(episode, _folder, Small, new CaptionStyle(), new Relay<RenderProgress>(progress.Add), default);

        var probe = await Ffmpeg.ProbeAsync(["-v", "error", "-show_entries", "stream=codec_name,width,height,r_frame_rate:format=duration", "-of", "default=nw=1", output], default);
        Assert.Contains("codec_name=h264", probe);
        Assert.Contains("codec_name=aac", probe);
        Assert.Contains("width=640", probe);
        Assert.Contains("height=360", probe);
        Assert.Contains("r_frame_rate=30/1", probe);

        var narration = await Builder.NarrateAsync(episode, _folder, default);
        var expected = FfmpegArgs.Timeline([.. narration.Select(n => n.Duration)])[^1] is var last ? last.Start + last.VideoLength : default;
        var actual = double.Parse(Regex.Match(probe, @"duration=([\d.]+)").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(actual, expected.TotalSeconds - 0.2, expected.TotalSeconds + 0.2);

        Assert.InRange(await Ffmpeg.MeasureLoudnessAsync(output, default), -17.0, -15.0);
        Assert.NotEmpty(progress);
        Assert.All(progress, p => Assert.InRange(p.Fraction, 0.0, 1.0));
        Assert.Equal(progress.Select(p => p.Fraction).Order(), progress.Select(p => p.Fraction)); // never goes backwards
        // Every stage is named in plain words, in order, with which clip it is on.
        var stages = progress.Select(p => p.Activity).Distinct().ToList();
        Assert.Contains(stages, s => s.StartsWith("Recording the voice for clip 1 of 3", StringComparison.Ordinal));
        Assert.Contains(stages, s => s.StartsWith("Drawing clips · 0 of 3 done", StringComparison.Ordinal));
        Assert.Contains(stages, s => s.StartsWith("Drawing clips · 3 of 3 done", StringComparison.Ordinal));
        Assert.Contains(stages, s => s.StartsWith("Joining the clips", StringComparison.Ordinal));
        Assert.True(stages.FindIndex(s => s.StartsWith("Recording", StringComparison.Ordinal)) < stages.FindIndex(s => s.StartsWith("Drawing", StringComparison.Ordinal)));
        Assert.True(stages.FindLastIndex(s => s.StartsWith("Drawing", StringComparison.Ordinal)) < stages.FindIndex(s => s.StartsWith("Joining", StringComparison.Ordinal)));
        Assert.Contains(stages, s => s.StartsWith("Drawing", StringComparison.Ordinal) && s.Contains("working on", StringComparison.Ordinal) && s.Contains(episode.Clips[1].Title));
        Assert.Equal(1.0, progress[^1].Fraction, precision: 2);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_folder, "export"))); // working folder cleaned up

        // Beside the video, ready to upload with it: a chapter per clip, subtitles and a cover picture.
        var name = Path.ChangeExtension(output, null);
        Assert.Equal(episode.Clips.Select(c => c.Title), File.ReadAllLines(name + ".chapters.txt").Select(line => line[(line.IndexOf(' ') + 1)..]));
        Assert.StartsWith("0:00 ", File.ReadAllText(name + ".chapters.txt"));
        Assert.StartsWith("1\n00:00:00,000 --> ", File.ReadAllText(name + ".srt"));
        Assert.True(File.Exists(name + ".thumbnail.png"));
    }

    /// <summary>Checkpoint evidence: the whole 16-clip mock episode at 1080p30. Takes minutes, so it is opt-in.</summary>
    [FfmpegFact]
    public async Task Full_mock_episode_exports_at_1080p30()
    {
        if (Environment.GetEnvironmentVariable("POAUTOROBO_SLOW_OUT") is not { Length: > 0 } keep)
            return;
        var episode = await new MockScriptWriter().WriteEpisodeAsync("Balancing the R1", Subject.UnitreeR1, [], EpisodeLength.Full, default);

        var output = await Builder.ExportAsync(episode, _folder, ExportPreset.Hd30, new CaptionStyle(), null, default);

        File.Copy(output, keep, overwrite: true);
    }

    [FfmpegFact]
    public async Task Narration_is_cached_so_unchanged_clips_are_not_synthesised_again()
    {
        var episode = await ThreeClipEpisodeAsync();
        var narrator = new CountingNarrator();
        var builder = new EpisodeBuilder(narrator, Ffmpeg);

        var first = await builder.NarrateAsync(episode, _folder, default);
        var edited = episode with { Clips = [episode.Clips[0], episode.Clips[1] with { ActiveTier = Tier.C }, episode.Clips[2]] };
        var second = await builder.NarrateAsync(edited, _folder, default);

        Assert.Equal(4, narrator.Calls); // three clips, then only the edited one
        Assert.Equal(first[0].Words.Select(w => w.Text), second[0].Words.Select(w => w.Text));
        Assert.Equal(first[0].Duration, second[0].Duration);
    }

    [FfmpegFact]
    public async Task Cancelling_an_export_leaves_no_file_behind()
    {
        var episode = await ThreeClipEpisodeAsync();
        using var cts = new CancellationTokenSource();
        await Builder.NarrateAsync(episode, _folder, default);
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Builder.ExportAsync(episode, _folder, ExportPreset.Uhd60, new CaptionStyle(), null, cts.Token));

        Assert.Empty(Directory.Exists(Path.Combine(_folder, "export")) ? Directory.GetFiles(Path.Combine(_folder, "export"), "*", SearchOption.AllDirectories) : []);
    }

    [FfmpegFact]
    public async Task A_failing_ffmpeg_run_reports_its_last_log_lines()
    {
        var error = await Assert.ThrowsAsync<FfmpegException>(() =>
            Ffmpeg.RunAsync(["-i", "does-not-exist.mp4", "out.mp4"], _folder, null, null, default));

        Assert.Contains("does-not-exist.mp4", error.Message);
    }

    private sealed class CountingNarrator : INarrator
    {
        private readonly MockNarrator _inner = new();
        public int Calls { get; private set; }

        public Task<Narration> SynthesizeAsync(string text, string outputPath, double rate, CancellationToken ct)
        {
            Calls++;
            return _inner.SynthesizeAsync(text, outputPath, rate, ct);
        }
    }
}
