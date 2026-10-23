using System.Globalization;
using System.Text.RegularExpressions;
using NSubstitute;

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

        var episode = await new MockScriptWriter().WriteEpisodeAsync(Lesson.Of("Balancing the R1"), [], EpisodeLength.Standard, default);
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

        var output = await Builder.ExportAsync(episode, _folder, Small, new CaptionStyle(), null, new Relay<RenderProgress>(progress.Add), default);

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

    [FfmpegFact]
    public async Task The_series_intro_and_outro_open_and_close_the_export_and_the_chapters_still_start_at_nought()
    {
        var three = await ThreeClipEpisodeAsync();
        // The film seen through the robot's eyes, and each clip a beat of its own, so a whoosh carries each one in.
        var episode = three with { Number = 7, Clips = [.. three.Clips.Select((c, i) => c with { Beat = $"Beat {i}", RobotView = c.Visual.UserVideoPath is not null })] };
        var kitFolder = Directory.CreateDirectory(Path.Combine(_folder, "series")).FullName;
        var brand = new Brand(new SeriesKit { ChannelName = "Teaching the R1", OutroSeconds = 6 }, kitFolder);

        var output = await Builder.ExportAsync(episode, _folder, Small, new CaptionStyle(), brand, null, default);

        Assert.StartsWith("07-", Path.GetFileName(output)); // sorts with the series
        var narration = await Builder.NarrateAsync(episode, _folder, default);
        var slots = FfmpegArgs.Timeline([EpisodeBuilder.IntroLength, .. narration.Select(n => n.Duration), TimeSpan.FromSeconds(6)]);
        var expected = slots[^1].Start + slots[^1].VideoLength;
        Assert.InRange(await Ffmpeg.ProbeDurationAsync(output, default), expected - TimeSpan.FromSeconds(0.2), expected + TimeSpan.FromSeconds(0.2));
        // YouTube shows chapters only when the first is at 0:00 and none is under ten seconds, so the intro joins the first clip's.
        var chapters = File.ReadAllLines(Path.ChangeExtension(output, null) + ".chapters.txt");
        Assert.Equal($"0:00 {episode.Clips[0].Title}", chapters[0]);
        Assert.Equal(episode.Clips.Count, chapters.Length);
        // The captions are where the clips are: after the intro.
        Assert.StartsWith($"1\n00:00:0{(int)slots[1].Start.TotalSeconds},", File.ReadAllText(Path.ChangeExtension(output, null) + ".srt"));

        // The preview's waveform reads every slot's sound after the render is done, the intro's and outro's included.
        var preview = await Builder.PreviewAsync(episode, _folder, brand, null, default);
        Assert.Equal(["Intro", .. episode.Clips.Select(c => c.Title), "Outro"], preview.Marks.Select(m => m.Title));
        Assert.All(preview.Marks, m => Assert.True(File.Exists(m.AudioPath), m.Title));
        // A kit with no sting of its own has one made up, so the intro is never silent.
        Assert.InRange(await Ffmpeg.MeasureLoudnessAsync(preview.Marks[0].AudioPath, default), -30.0, 0.0);

        // Without a channel name there is nothing to put in an intro, so the episode is exported without one.
        var plain = await Builder.ExportAsync(episode, _folder, Small, new CaptionStyle(), brand with { Kit = new SeriesKit() }, null, default);
        var bare = FfmpegArgs.Timeline([.. narration.Select(n => n.Duration)])[^1];
        Assert.InRange(await Ffmpeg.ProbeDurationAsync(plain, default), bare.Start + bare.VideoLength - TimeSpan.FromSeconds(0.2), bare.Start + bare.VideoLength + TimeSpan.FromSeconds(0.2));
    }

    [FfmpegFact]
    public async Task Three_teasers_are_cut_upright_from_the_finished_clips_without_asking_for_anything_new()
    {
        // Lines long enough that the clips between them hold ten-second teasers.
        const string Line = "The robot leans forward, lifts one foot onto the first step, shifts its weight across, and then brings the other foot up beside it.";
        var three = await ThreeClipEpisodeAsync();
        var episode = three with { Number = 3, Clips = [.. three.Clips.Select(c => EpisodeEditor.WithDialogue(c, Line))] };
        var voice = new MockNarrator();
        var narrator = Substitute.For<INarrator>();
        narrator.SynthesizeAsync(default!, default!, default, default).ReturnsForAnyArgs(call =>
            voice.SynthesizeAsync(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<double>(2), call.ArgAt<CancellationToken>(3)));
        var builder = new EpisodeBuilder(narrator, Ffmpeg);
        var lengths = await builder.ClipLengthsAsync(episode, _folder, default); // the voices, recorded as for the master
        narrator.ClearReceivedCalls();

        var teasers = Teasers.ByRule(episode, lengths);
        var folder = await builder.ExportTeasersAsync(episode, _folder, teasers, new CaptionStyle(), new Brand(new SeriesKit { ChannelName = "Teaching the R1" }, _folder), null, default);

        await narrator.DidNotReceiveWithAnyArgs().SynthesizeAsync(default!, default!, default, default); // nothing new is made, or paid for
        var files = Directory.GetFiles(folder, "*.mp4");
        Assert.InRange(teasers.Count, 2, Teasers.MaxTeasers); // three unless two would be the same cut
        Assert.Equal(teasers.Count, files.Length);
        Assert.All(files, file => Assert.StartsWith("03-", Path.GetFileName(file)));
        foreach (var file in files)
        {
            var probe = await Ffmpeg.ProbeAsync(["-v", "error", "-show_entries", "stream=codec_name,width,height,r_frame_rate", "-of", "default=nw=1", file], default);
            Assert.Contains("codec_name=h264", probe);
            Assert.Contains("codec_name=aac", probe);
            Assert.Contains("width=1080", probe);
            Assert.Contains("height=1920", probe);
            Assert.Contains("r_frame_rate=30/1", probe);
            Assert.InRange(await Ffmpeg.ProbeDurationAsync(file, default), Teasers.Target - Teasers.Tolerance, Teasers.Target + Teasers.Tolerance);
        }
    }

    [FfmpegFact]
    public async Task A_clip_or_a_teaser_is_watched_on_its_own_from_the_pictures_the_preview_drew()
    {
        var episode = await ThreeClipEpisodeAsync();
        var brand = new Brand(new SeriesKit { ChannelName = "Teaching the R1" }, Directory.CreateDirectory(Path.Combine(_folder, "series")).FullName);
        var cache = Path.Combine(_folder, "clips");
        var builder = new EpisodeBuilder(new MockNarrator(), Ffmpeg) { ClipCacheFolder = cache };
        await builder.PreviewAsync(episode, _folder, brand, null, default);
        var drawn = Directory.GetFiles(cache).Length;

        var clip = await builder.ClipPreviewAsync(episode, _folder, 1, brand, default);

        // The preview's own drawing of the clip is used and only its sound is added: one new file, and none the second time.
        Assert.Equal(drawn + 1, Directory.GetFiles(cache).Length);
        Assert.Equal(clip, await builder.ClipPreviewAsync(episode, _folder, 1, brand, default));
        Assert.Equal(drawn + 1, Directory.GetFiles(cache).Length);
        var probe = await Ffmpeg.ProbeAsync(["-v", "error", "-show_entries", "stream=codec_name,width,height", "-of", "default=nw=1", clip], default);
        Assert.Contains("codec_name=h264", probe);
        Assert.Contains("codec_name=aac", probe);
        Assert.Contains("width=640", probe);
        var voice = (await builder.NarrateClipAsync(episode.Clips[1], _folder, default)).Duration;
        Assert.InRange(await Ffmpeg.ProbeDurationAsync(clip, default), voice - TimeSpan.FromSeconds(0.3), voice + TimeSpan.FromSeconds(0.1));

        // A teaser's draft is small and upright, filled to the frame when asked, and cut from clips already drawn.
        var lengths = await builder.ClipLengthsAsync(episode, _folder, default);
        var teaser = new Teaser("Watch it climb", [new(episode.Clips[0].Id, TimeSpan.Zero, lengths[episode.Clips[0].Id]), new(episode.Clips[1].Id, TimeSpan.Zero, TimeSpan.FromSeconds(1))], Fill: true);
        var before = Directory.GetFiles(cache).Length;
        var draft = await builder.TeaserDraftAsync(episode, _folder, teaser, new CaptionStyle(), brand, default);
        var upright = await Ffmpeg.ProbeAsync(["-v", "error", "-show_entries", "stream=codec_name,width,height", "-of", "default=nw=1", draft], default);
        Assert.Contains("width=360", upright);
        Assert.Contains("height=640", upright);
        Assert.Contains("codec_name=aac", upright);
        Assert.Equal(before + 1, Directory.GetFiles(cache).Length);
    }

    [FfmpegFact]
    public async Task A_film_that_keeps_its_own_sound_is_heard_in_the_export_and_no_voice_is_recorded_for_it()
    {
        // A film whose sound stops a second before its picture, and one with no sound track at all.
        var (falls, silent) = (Path.Combine(_folder, "falls.mp4"), Path.Combine(_folder, "silent.mp4"));
        await Ffmpeg.RunAsync(["-y", "-f", "lavfi", "-i", "testsrc=s=320x240:r=25:d=3", "-f", "lavfi", "-i", "sine=f=440:d=2", "-pix_fmt", "yuv420p", "-c:a", "aac", falls], _folder, null, null, default);
        await Ffmpeg.RunAsync(["-y", "-f", "lavfi", "-i", "testsrc=s=320x240:r=25:d=2", "-pix_fmt", "yuv420p", silent], _folder, null, null, default);
        var narrator = TestExtensions.SilentNarrator();
        var builder = new EpisodeBuilder(narrator, Ffmpeg);
        var written = await new MockScriptWriter().WriteEpisodeAsync(Lesson.Of("Climbing stairs"), [], new EpisodeLength(2, 2), default);
        var episode = written with
        {
            Clips =
            [
                EpisodeEditor.WithDialogue(BeatPlan.WithFootage(written.Clips[0], new FootageNote(falls, "falls", OwnSound: true, TimeSpan.FromSeconds(3))), "It falls over"),
                BeatPlan.WithFootage(written.Clips[1], new FootageNote(silent, "", OwnSound: true, TimeSpan.FromSeconds(2))),
            ],
        };

        // What was heard said in the first film is kept beside it; the typed words, as many, take its times.
        File.WriteAllText(Clip.HeardFor(falls), System.Text.Json.JsonSerializer.Serialize(new[] { new WordTiming("it", TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.3)), new WordTiming("falls", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.4)), new WordTiming("over", TimeSpan.FromSeconds(1.6), TimeSpan.FromSeconds(0.4)) }));

        var sound = await builder.NarrateAsync(episode, _folder, default);

        await narrator.DidNotReceiveWithAnyArgs().SynthesizeAsync(default!, default!, default, default); // nothing is spoken, or paid for
        Assert.Equal([3.0, 2.0], sound.Select(s => Math.Round(s.Duration.TotalSeconds, 1))); // each as long as its film
        Assert.Equal(["It", "falls", "over"], sound[0].Words.Select(w => w.Text)); // what was typed becomes its captions
        Assert.Equal([0.2, 1.0, 1.6], sound[0].Words.Select(w => w.Start.TotalSeconds)); // at the times it was heard said
        Assert.Empty(sound[1].Words);

        // Listening sends the film's sound alone, as speech; a film with no sound is not listened to at all.
        var heard = await builder.HearAsync(falls, (wav, _) =>
        {
            Assert.Equal(2.0, Math.Round(WavInfo.Duration(wav).TotalSeconds, 1));
            return Task.FromResult<IReadOnlyList<WordTiming>>([new("Beep", TimeSpan.Zero, TimeSpan.FromSeconds(2))]);
        }, default);
        Assert.Equal("Beep", Assert.Single(heard).Text);
        Assert.Empty(await builder.HearAsync(silent, (_, _) => throw new InvalidOperationException("Asked to listen to silence."), default));
        // A film is watched from a few frames spread across it.
        var frames = await builder.FramesAsync(falls, 3, default);
        Assert.Equal([0.5, 1.5, 2.5], frames.Select(f => Math.Round(f.At.TotalSeconds, 1)));
        Assert.All(frames, f => Assert.True(File.Exists(f.Path)));
        Directory.Delete(Path.GetDirectoryName(frames[0].Path)!, recursive: true);

        var output = await builder.ExportAsync(episode, _folder, Small, new CaptionStyle(), null, null, default);
        // The film's own tone is heard: silence would measure about -70. (Five seconds of tone and gaps is too short
        // for the leveller to land exactly on -16, so only "heard" is asked here; the long export test checks the level.)
        Assert.InRange(await Ffmpeg.MeasureLoudnessAsync(output, default), -25.0, -10.0);
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
            Builder.ExportAsync(episode, _folder, ExportPreset.Uhd60, new CaptionStyle(), null, null, cts.Token));

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
