using System.Globalization;

namespace PoAutoRobo.Core.Tests;

public sealed class AssCaptionsTests
{
    /// <summary>Each word lasts 0.3s with a 0.1s gap, so word n starts at 0.4n seconds.</summary>
    private static List<WordTiming> Timed(string text) =>
        [.. Durations.SplitWords(text).Select((w, i) => new WordTiming(w, TimeSpan.FromSeconds(0.4 * i), TimeSpan.FromSeconds(0.3)))];

    private static readonly CaptionSegment[] TwoClips =
    [
        new(TimeSpan.Zero, Timed("The robot keeps its balance. Tiny corrections happen every few milliseconds, all day long!")),
        new(TimeSpan.FromSeconds(10), Timed("Next we look at the reward function.")),
    ];

    private static List<(TimeSpan Start, TimeSpan End, string Text)> Events(string ass) =>
        [.. ass.Split('\n').Where(l => l.StartsWith("Dialogue:", StringComparison.Ordinal)).Select(l =>
        {
            var f = l.TrimEnd('\r').Split(',', 10);
            return (Time(f[1]), Time(f[2]), f[9]);
        })];

    private static TimeSpan Time(string s) => TimeSpan.ParseExact(s, @"h\:mm\:ss\.ff", CultureInfo.InvariantCulture);

    private static string Plain(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\{[^}]*\}", "").Replace(@"\N", " ");

    [Fact]
    public async Task Preset_snapshot()
    {
        await Preset_snapshotCase(CaptionPreset.KaraokeHighlight);
        await Preset_snapshotCase(CaptionPreset.TwoLineBlock);
        await Preset_snapshotCase(CaptionPreset.ComicBanner);
    }

    private Task Preset_snapshotCase(CaptionPreset preset) =>
        Verify(AssCaptions.Build(TwoClips, new CaptionStyle(preset))).UseTextForParameters($"preset={preset}");

    [Fact]
    public void Every_preset_shows_each_caption_exactly_when_its_first_word_is_spoken_and_never_across_two_clips()
    {
        var spoken = TwoClips.SelectMany(s => s.Words.Select(w => (Start: s.Offset + w.Start, w.Text))).ToList();

        Assert.All(Enum.GetValues<CaptionPreset>(), preset =>
        {
            var events = Events(AssCaptions.Build(TwoClips, new CaptionStyle(preset)));

            if (preset == CaptionPreset.KaraokeHighlight)
            {
                // One event per word, starting as that word is spoken: this is what lights the word in time.
                Assert.Equal(spoken.Select(w => w.Start), events.Select(e => e.Start));
            }
            else
            {
                Assert.Equal(string.Join(' ', spoken.Select(w => w.Text)), string.Join(' ', events.Select(e => Plain(e.Text))), ignoreCase: true);
                var index = 0;
                foreach (var e in events)
                {
                    Assert.Equal(spoken[index].Start, e.Start);
                    index += Durations.WordCount(Plain(e.Text));
                }
            }
            Assert.All(events, e => Assert.True(e.End <= TimeSpan.FromSeconds(10) || e.Start >= TimeSpan.FromSeconds(10)));
            Assert.All(events.Zip(events.Skip(1)), p => Assert.True(p.First.End <= p.Second.Start));
        });
    }

    [Fact]
    public void Style_controls_reach_the_file_and_a_bad_colour_is_rejected()
    {
        var ass = AssCaptions.Build(TwoClips, new CaptionStyle(CaptionPreset.KaraokeHighlight, FontSize: 72, AccentColor: "#11AAFF", StrokeWidth: 6));

        Assert.Contains(",72,", ass);
        Assert.Contains("&H00FFAA11", ass); // ASS colours are blue-green-red
        Assert.Matches(@"Style: [^\n]*,1,6,0,2,", ass);
        // The colour is written into the style line as it stands, so anything but #RRGGBB must never get that far.
        Assert.Throws<ArgumentException>(() => AssCaptions.Build(TwoClips, new CaptionStyle(CaptionPreset.CleanSubtitle, AccentColor: "yellow")));
    }

    [Fact]
    public void Text_that_looks_like_formatting_codes_is_neutralised()
    {
        CaptionSegment[] segment = [new(TimeSpan.Zero, Timed(@"use {\b1}bold\N here"))];

        var text = Events(AssCaptions.Build(segment, new CaptionStyle(CaptionPreset.CleanSubtitle))).Single().Text;

        Assert.DoesNotContain('{', text);
        Assert.DoesNotContain('\\', text);
    }
}
