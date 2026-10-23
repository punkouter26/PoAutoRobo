namespace PoAutoRobo.Core.Render;

// The ten-second upright teasers, cut from the clips already made.
public sealed partial class EpisodeBuilder
{
    /// <summary>
    /// How long each clip's picture runs in the finished episode: the lengths teasers are cut within. Records any voice
    /// not yet recorded, which an episode ready to export has already done.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, TimeSpan>> ClipLengthsAsync(Episode episode, string folder, CancellationToken ct)
    {
        var narrations = new List<Narration>(episode.Clips.Count);
        foreach (var clip in episode.Clips)
            narrations.Add(await NarrateClipAsync(clip, folder, ct));
        var slots = FfmpegArgs.Timeline([.. narrations.Select(n => n.Duration)]);
        return episode.Clips.Select((c, i) => (c.Id, slots[i].VideoLength)).ToDictionary(p => p.Id, p => p.VideoLength);
    }

    /// <summary>
    /// Renders each teaser as an upright video: its moments cut from the clips' finished pictures (drawn at 1080p
    /// without captions, through the clip cache, and only for the clips the teasers use) and from their sound.
    /// Nothing is generated: no picture, voice or model is asked for anything.
    /// </summary>
    /// <returns>The folder holding them, inside the episode's export folder.</returns>
    public async Task<string> ExportTeasersAsync(Episode episode, string folder, IReadOnlyList<Teaser> teasers, CaptionStyle captions, Brand? brand,
        IProgress<RenderProgress>? progress, CancellationToken ct)
    {
        const double DrawShare = 0.6; // drawing the clips is most of the work; each teaser is then one short encode
        var output = Path.Combine(folder, "export", "teasers");
        Directory.CreateDirectory(output);
        var narrations = new List<Narration>(episode.Clips.Count);
        foreach (var clip in episode.Clips)
            narrations.Add(await NarrateClipAsync(clip, folder, ct));
        var slots = FfmpegArgs.Timeline([.. narrations.Select(n => n.Duration)]);
        var place = episode.Clips.Select((c, i) => (c.Id, i)).ToDictionary(p => p.Id, p => p.i);
        var used = teasers.SelectMany(t => t.Moments).Select(m => place[m.ClipId]).Distinct().Order().ToList();

        var work = Files.NewScratchFolder("teasers");
        ForgetOldClips();
        try
        {
            CopyTitleFont(work);
            var pictures = await DrawClipsAsync(episode, i => narrations[i].Words, i => slots[i], ExportPreset.Hd30, null, used, work, new MediaCache(ClipCacheFolder),
                (activity, fraction, clips) => progress?.Report(new RenderProgress(activity, DrawShare * fraction, clips)), ct);
            var number = episode.Number > 0 ? $"{episode.Number:00}-" : "";
            for (var t = 0; t < teasers.Count; t++)
            {
                var teaser = teasers[t];
                progress?.Report(new RenderProgress($"Cutting teaser {t + 1} of {teasers.Count} · {teaser.Hook}", DrawShare + (1 - DrawShare) * t / teasers.Count));
                var (args, _) = await TeaserCutAsync(teaser, narrations, place, pictures, captions, Bookends(brand), null, work, ct);
                var file = Path.Combine(output, $"{number}{ProjectStore.Slug(episode.Title)}-teaser-{t + 1}.mp4");
                await ffmpeg.RunAsync(args(file), work, teaser.Length, null, ct);
            }
            progress?.Report(new RenderProgress("Done", 1));
            return output;
        }
        finally
        {
            DeleteScratch(work);
        }
    }

    /// <summary>
    /// One teaser, small, to watch on its card before the full render: cut from the same clip pictures the quick
    /// preview draws, so after a preview it takes a moment. Kept in the clip cache, so watching it again is instant.
    /// Nothing is generated.
    /// </summary>
    /// <returns>The video's path.</returns>
    public async Task<string> TeaserDraftAsync(Episode episode, string folder, Teaser teaser, CaptionStyle captions, Brand? brand, CancellationToken ct)
    {
        var narrations = new List<Narration>(episode.Clips.Count);
        foreach (var clip in episode.Clips)
            narrations.Add(await NarrateClipAsync(clip, folder, ct));
        var slots = FfmpegArgs.Timeline([.. narrations.Select(n => n.Duration)]); // as the full teasers lay them out
        var place = episode.Clips.Select((c, i) => (c.Id, i)).ToDictionary(p => p.Id, p => p.i);
        var used = teaser.Moments.Select(m => place[m.ClipId]).Distinct().Order().ToList();
        var work = Files.NewScratchFolder("teaser-draft");
        var cache = new MediaCache(ClipCacheFolder);
        try
        {
            CopyTitleFont(work);
            var pictures = await DrawClipsAsync(episode, i => narrations[i].Words, i => slots[i], Draft, null, used, work, cache, null, ct);
            var (args, made) = await TeaserCutAsync(teaser, narrations, place, pictures, captions, Bookends(brand), ExportPreset.UprightDraft, work, ct);
            // The command names the clip pictures and sounds, whose own names change with what is in them.
            return await cache.GetOrCreateAsync([string.Join('\n', args("teaser.mp4")), .. made], ".mp4", scratch => ffmpeg.RunAsync(args(scratch), work, teaser.Length, null, ct));
        }
        finally
        {
            DeleteScratch(work);
        }
    }

    /// <summary>
    /// Writes a teaser's hook, captions and channel name into the working folder, and gives its command line for any
    /// output path, with the words that decide how it looks besides the command itself.
    /// </summary>
    /// <param name="brand">The kit, when it names a channel; null for no channel bar.</param>
    /// <param name="size">The frame; null for the full upright size.</param>
    private static async Task<(Func<string, IReadOnlyList<string>> Args, IReadOnlyList<string> Made)> TeaserCutAsync(Teaser teaser, IReadOnlyList<Narration> narrations,
        IReadOnlyDictionary<Guid, int> place, string?[] pictures, CaptionStyle captions, Brand? brand, ExportPreset? size, string work, CancellationToken ct)
    {
        // Each moment's words, timed from where the moment falls in the teaser, become its captions.
        var segments = new List<CaptionSegment>();
        var at = TimeSpan.Zero;
        foreach (var moment in teaser.Moments)
        {
            var words = narrations[place[moment.ClipId]].Words.Where(w => w.Start >= moment.Start && w.Start < moment.Start + moment.Length).ToList();
            segments.Add(new CaptionSegment(at - moment.Start, words));
            at += moment.Length;
        }
        var (hook, ass, channel) = (FfmpegArgs.HookLines(teaser.Hook), AssCaptions.Build(segments, captions, portrait: true), brand?.Kit.ChannelName.Trim());
        // A file for each line of the hook, so each can drop in on its own.
        var lines = hook.Split('\n');
        var hookFiles = lines.Select((_, i) => $"teaser_hook_{i}.txt").ToList();
        for (var i = 0; i < lines.Length; i++)
            await File.WriteAllTextAsync(Path.Combine(work, hookFiles[i]), lines[i], ct);
        await File.WriteAllTextAsync(Path.Combine(work, "teaser.ass"), ass, ct);
        if (channel is not null)
            await File.WriteAllTextAsync(Path.Combine(work, "teaser_channel.txt"), channel, ct);
        List<TeaserCut> cuts = [.. teaser.Moments.Select(m => new TeaserCut(pictures[place[m.ClipId]]!, narrations[place[m.ClipId]].AudioPath, m.Start, m.Length))];
        return (to => FfmpegArgs.TeaserVideo(cuts, hookFiles, channel is null ? null : "teaser_channel.txt", "teaser.ass", to, teaser.Fill, size), [hook, ass, channel ?? ""]);
    }
}
