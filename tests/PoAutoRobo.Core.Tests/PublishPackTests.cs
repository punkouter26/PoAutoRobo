
namespace PoAutoRobo.Core.Tests;

public sealed class PublishPackTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Chapters_are_one_line_per_clip_with_its_start_as_minutes_and_seconds()
    {
        var chapters = PublishPack.Chapters([(S(0), "Meet the R1"), (S(75.4), "Why balance is hard"), (S(3725), "What to try next")]);

        // Video sites read this form, and need the first chapter at 0:00; past the hour the minutes keep counting.
        Assert.Equal("0:00 Meet the R1\n1:15 Why balance is hard\n62:05 What to try next\n", chapters);

        // The upload details are laid out to copy from, a part at a time.
        var notes = PublishPack.Description(new PublishNotes(["One", "Two"], "About it.", ["robots", "balance"], ["#r1"]));
        Assert.Equal("TITLES\nOne\nTwo\n\nDESCRIPTION\nAbout it.\n\n#r1\n\nTAGS\nrobots, balance\n", notes);
    }

    [Fact]
    public void Subtitles_are_numbered_plain_lines_timed_on_the_episode_clock_with_formatting_codes_removed()
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
}
