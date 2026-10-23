
namespace PoAutoRobo.Core.Tests;

public sealed class PublishTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Chapters_and_subtitles_are_written_as_video_sites_read_them()
    {
        Chapters_are_one_line_per_clip_with_its_start_as_minutes_and_seconds();
        Subtitles_are_numbered_plain_lines_timed_on_the_episode_clock_with_formatting_codes_removed();
    }

    private void Chapters_are_one_line_per_clip_with_its_start_as_minutes_and_seconds()
    {
        var chapters = PublishPack.Chapters([(S(0), "Meet the R1"), (S(75.4), "Why balance is hard"), (S(3725), "What to try next")]);

        // Video sites read this form, and need the first chapter at 0:00; past the hour the minutes keep counting.
        Assert.Equal("0:00 Meet the R1\n1:15 Why balance is hard\n62:05 What to try next\n", chapters);

        // The upload details are laid out to copy from, a part at a time.
        var notes = PublishPack.Description(new PublishNotes(["One", "Two"], "About it.", ["robots", "balance"], ["#r1"]));
        Assert.Equal("TITLES\nOne\nTwo\n\nDESCRIPTION\nAbout it.\n\n#r1\n\nTAGS\nrobots, balance\n", notes);

        // Every title carries the series' name and the episode's number, so the uploads read as one series.
        var suggested = new PublishNotes(["Walking up stairs", "Teaching the R1 #7: Stairs at last"], "", [], []);
        Assert.Equal(["Teaching the R1 #7: Walking up stairs", "Teaching the R1 #7: Stairs at last"], suggested.InSeries("Teaching the R1", 7).Titles);
        Assert.Equal(["Teaching the R1: Walking up stairs", "Teaching the R1 #7: Stairs at last"], suggested.InSeries("Teaching the R1", 0).Titles); // unnumbered
        Assert.Same(suggested, suggested.InSeries(" ", 7)); // no channel name: the titles stand as written
    }

    private void Subtitles_are_numbered_plain_lines_timed_on_the_episode_clock_with_formatting_codes_removed()
    {
        CaptionSegment[] segments =
        [
            new(S(0), [new WordTiming("Hello", S(0), S(0.3)), new WordTiming(@"{\b1}there.", S(0.4), S(0.3)), new WordTiming("Next.", S(0.8), S(0.2))]),
            new(S(3661.5), [new WordTiming("Bye.", S(0), S(0.5))]),
        ];

        var srt = PublishPack.Srt(segments);

        Assert.Equal(
            "1\n00:00:00,000 --> 00:00:00,700\nHello b1there.\n\n" +
            "2\n00:00:00,800 --> 00:00:01,000\nNext.\n\n" +
            "3\n01:01:01,500 --> 01:01:02,000\nBye.\n\n",
            srt);
    }

    // ---- Teasers ----

    [Fact]
    public void Teasers_are_ten_seconds_of_real_moments_under_a_short_hook_whether_suggested_by_rule_or_held_to_it()
    {
        var episode = ProjectStoreTests.NewEpisode(5) with { Title = "Walking up three stairs without the handrail at last" };
        string[] beats = ["Goal", "Try", "Fail", "Result", "Recap"];
        episode = episode with { Clips = [.. episode.Clips.Select((c, i) => c with { Beat = beats[i] })] };
        double[] seconds = [6, 12, 4, 8, 20];
        var lengths = episode.Clips.Select((c, i) => (c.Id, Length: S(seconds[i]))).ToDictionary(p => p.Id, p => p.Length);
        Guid Clip(int i) => episode.Clips[i].Id;

        // By rule, with nothing asked of any service: the opening of the footage, the result, and the ending.
        var byRule = Teasers.ByRule(episode, lengths);
        Assert.Equal(3, byRule.Count);
        Assert.All(byRule, Holds);
        Assert.Equal(Clip(1), byRule[0].Moments[0].ClipId); // the first clip in a beat for footage
        Assert.Contains(byRule[1].Moments, m => m.ClipId == Clip(3)); // the result
        Assert.Equal(Clip(4), byRule[2].Moments[^1].ClipId); // the very end
        Assert.Equal(lengths[Clip(4)], byRule[2].Moments[^1].Start + byRule[2].Moments[^1].Length);

        // Suggestions that stray are held to the rules, or dropped when they cannot be.
        Teaser[] suggested =
        [
            new("one two three four five six seven eight nine ten eleven twelve", [new(Clip(1), S(10), S(20))]), // runs past its clip and too long
            new("Too short to save", [new(Clip(2), S(0), S(3))]),                                               // a four-second clip has no ten seconds in it
            new("Nowhere", [new(Guid.NewGuid(), S(0), S(10))]),                                                 // a clip that is not there
            new("Three moments", [new(Clip(0), S(1), S(3)), new(Clip(2), S(0), S(3)), new(Clip(3), S(2), S(3)), new(Clip(4), S(0), S(5))], Fill: true), // a fourth is one too many
            new("A flash at the end", [new(Clip(1), S(0), S(5)), new(Clip(3), S(0), S(4.2)), new(Clip(4), S(0), S(0.6))]), // too short to see, and its time made up
        ];
        var held = Teasers.Fit(suggested, lengths);
        Assert.All(held, Holds);
        Assert.Equal(["one two three four five six seven eight", "Three moments", "A flash at the end"], held.Select(t => t.Hook));
        Assert.Equal([false, true, false], held.Select(t => t.Fill)); // how each is framed is the owner's choice, kept through the fitting
        Assert.Equal(2, held[2].Moments.Count);
        Assert.Equal(3, held[1].Moments.Count);
        Assert.Equal((Clip(1), S(2)), (held[0].Moments[0].ClipId, held[0].Moments[0].Start)); // moved back so ten seconds fit inside the clip

        void Holds(Teaser teaser)
        {
            Assert.InRange(teaser.Length, Teasers.Target - Teasers.Tolerance, Teasers.Target + Teasers.Tolerance);
            Assert.InRange(teaser.Moments.Count, 1, Teasers.MaxMoments);
            Assert.InRange(Durations.WordCount(teaser.Hook), 0, Teasers.MaxHookWords);
            Assert.All(teaser.Moments, m => Assert.True(m.Start >= TimeSpan.Zero && m.Start + m.Length <= lengths[m.ClipId] && m.Length >= Teasers.MinMoment, $"{m}"));
        }
    }
}
