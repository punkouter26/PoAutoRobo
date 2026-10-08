using System.Globalization;
using PoAutoRobo.Core.Models;
using PoAutoRobo.Core.Pipeline;
using PoAutoRobo.Core.Services;

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
        [.. ass.Split('\n').Where(l => l.StartsWith("Dialogue:")).Select(l =>
        {
            var f = l.TrimEnd('\r').Split(',', 10);
            return (Time(f[1]), Time(f[2]), f[9]);
        })];

    private static TimeSpan Time(string s) => TimeSpan.ParseExact(s, @"h\:mm\:ss\.ff", CultureInfo.InvariantCulture);

    private static string Plain(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\{[^}]*\}", "").Replace(@"\N", " ");

    [Theory]
    [InlineData(CaptionPreset.KaraokeHighlight)]
    [InlineData(CaptionPreset.TwoLineBlock)]
    [InlineData(CaptionPreset.CleanSubtitle)]
    [InlineData(CaptionPreset.ComicBanner)]
    public Task Preset_snapshot(CaptionPreset preset) =>
        Verify(AssCaptions.Build(TwoClips, new CaptionStyle(preset))).UseParameters(preset);

    [Fact]
    public void Karaoke_has_one_event_per_word_starting_exactly_when_the_word_is_spoken()
    {
        var events = Events(AssCaptions.Build(TwoClips, new CaptionStyle(CaptionPreset.KaraokeHighlight)));
        var expected = TwoClips.SelectMany(s => s.Words.Select(w => s.Offset + w.Start)).ToList();

        Assert.Equal(expected, events.Select(e => e.Start));
    }

    [Theory]
    [InlineData(CaptionPreset.TwoLineBlock)]
    [InlineData(CaptionPreset.CleanSubtitle)]
    [InlineData(CaptionPreset.ComicBanner)]
    public void Block_presets_show_every_word_in_order_and_start_with_their_first_word(CaptionPreset preset)
    {
        var events = Events(AssCaptions.Build(TwoClips, new CaptionStyle(preset)));
        var spoken = TwoClips.SelectMany(s => s.Words.Select(w => (Start: s.Offset + w.Start, w.Text))).ToList();

        Assert.Equal(
            string.Join(' ', spoken.Select(w => w.Text)),
            string.Join(' ', events.Select(e => Plain(e.Text))),
            ignoreCase: true);
        var index = 0;
        foreach (var e in events)
        {
            Assert.Equal(spoken[index].Start, e.Start);
            index += Durations.WordCount(Plain(e.Text));
        }
    }

    [Theory]
    [InlineData(CaptionPreset.KaraokeHighlight)]
    [InlineData(CaptionPreset.TwoLineBlock)]
    public void Captions_never_span_two_clips_or_overlap(CaptionPreset preset)
    {
        var events = Events(AssCaptions.Build(TwoClips, new CaptionStyle(preset)));

        Assert.All(events, e => Assert.True(e.End <= TimeSpan.FromSeconds(10) || e.Start >= TimeSpan.FromSeconds(10)));
        Assert.All(events.Zip(events.Skip(1)), p => Assert.True(p.First.End <= p.Second.Start));
    }

    [Fact]
    public void Style_controls_reach_the_file()
    {
        var ass = AssCaptions.Build(TwoClips, new CaptionStyle(CaptionPreset.KaraokeHighlight, FontSize: 72, AccentColor: "#11AAFF", StrokeWidth: 6));

        Assert.Contains(",72,", ass);
        Assert.Contains("&H00FFAA11", ass); // ASS colours are blue-green-red
        Assert.Matches(@"Style: [^\n]*,1,6,0,2,", ass);
    }

    [Fact]
    public void Text_that_looks_like_formatting_codes_is_neutralised()
    {
        CaptionSegment[] segment = [new(TimeSpan.Zero, Timed(@"use {\b1}bold\N here"))];

        var text = Events(AssCaptions.Build(segment, new CaptionStyle(CaptionPreset.CleanSubtitle))).Single().Text;

        Assert.DoesNotContain('{', text);
        Assert.DoesNotContain('\\', text);
    }

    [Fact]
    public void Bad_accent_colour_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => AssCaptions.Build(TwoClips, new CaptionStyle(CaptionPreset.CleanSubtitle, AccentColor: "yellow")));
    }
}
