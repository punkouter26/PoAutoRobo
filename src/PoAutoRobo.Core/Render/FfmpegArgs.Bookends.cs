using System.Globalization;

namespace PoAutoRobo.Core.Render;

// The series intro and outro: their cards, the swarm of light and the stills of them.
public static partial class FfmpegArgs
{
    /// <summary>
    /// The series intro: the channel's name, its tagline and "#N · title" rising in one after another over a slow
    /// gradient in the accent colour, the logo in the corner and the mascot beside them. Built here, so it costs nothing.
    /// </summary>
    /// <param name="channelFile">Text files in the working folder, as for a title card: the words are drawn, never run.</param>
    public static IReadOnlyList<string> IntroVideo(BookendLook look, string channelFile, string? taglineFile, string titleFile, TimeSpan length, ExportPreset preset, string output) =>
        Card(look, length, preset, output, IntroWords(look, channelFile, taglineFile, titleFile));

    // Side: with a mascot on the right the words keep to the left; without one, they are centred. Centre: centred,
    // under the mascot when there is one. Lower: on a dark band across the lower third, clear of the mascot.
    private static IReadOnlyList<string> IntroWords(BookendLook look, string channelFile, string? taglineFile, string titleFile)
    {
        var (x, channel, tagline, title) = look.Layout switch
        {
            IntroLayout.Centre when look.Mascot is not null => ("(w-text_w)/2", "h*0.54", "h*0.67", "h*0.76"),
            IntroLayout.Centre => ("(w-text_w)/2", "h*0.34", "h*0.48", "h*0.60"),
            IntroLayout.Lower => ("w*0.06", "h*0.64", "h*0.76", "h*0.82"),
            _ => (look.Mascot is null ? "(w-text_w)/2" : "w*0.08", "h*0.34", "h*0.48", "h*0.60"),
        };
        return
        [
            .. look.Layout == IntroLayout.Lower ? new[] { "drawbox=x=0:y=ih*0.61:w=iw:h=ih*0.31:color=black@0.45:t=fill" } : [],
            Words(channelFile, "h/10", x, channel, 0.8),
            .. taglineFile is null ? [] : new[] { Words(taglineFile, "h/24", x, tagline, 1.2) },
            Words(titleFile, "h/14", x, title, 1.6),
        ];
    }

    /// <summary>
    /// One frame of the intro as it stands once everything has come in, over black instead of its background, as plain
    /// colour values (three bytes a pixel): where the swarm's points of light land, and in which colours.
    /// </summary>
    public static IReadOnlyList<string> IntroMask(BookendLook look, string channelFile, string? taglineFile, string titleFile, ExportPreset size, string output)
    {
        var card = Card(look, IntroMaskLength, size, output, IntroWords(look, channelFile, taglineFile, titleFile), black: true).ToList();
        var end = card.IndexOf("-map") + 2; // the inputs, the drawing and its output; the encoding is replaced
        return [.. card.Take(end), "-ss", "2.5", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-y", output];
    }

    // Long enough that every line has faded in by the frame taken, and the card's own fade out has not begun.
    private static readonly TimeSpan IntroMaskLength = TimeSpan.FromSeconds(4);

    /// <summary>Plain RGBA pixels the app worked out (see <see cref="Emblem"/>), saved as a PNG that keeps their see-through parts.</summary>
    public static IReadOnlyList<string> Picture(string pixelsFile, int width, int height, string output) =>
        ["-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{width}x{height}", "-i", pixelsFile, "-frames:v", "1", "-y", output];

    /// <summary>
    /// One frame, <paramref name="at"/> into an intro or outro card as <see cref="IntroVideo"/> or <see cref="OutroVideo"/>
    /// draws it, as a picture: the card's own drawing, so the still shows exactly what the video will.
    /// </summary>
    public static IReadOnlyList<string> Still(IReadOnlyList<string> card, TimeSpan at, string output)
    {
        var end = card.ToList().IndexOf("-map") + 2; // the inputs, the drawing and its output; the encoding is replaced
        return [.. card.Take(end), "-ss", Seconds(at), "-frames:v", "1", "-y", output];
    }

    /// <summary>
    /// The intro with its swarm: the swarm's frames (read from FFmpeg's input, drawn at <paramref name="swarmSize"/>) with
    /// a soft glow, laid over the dark; the background rising out of it into the hit; and the finished card coming up
    /// under them as they land, so the points of light become the real words, logo and mascot. Encoded like every clip.
    /// </summary>
    /// <param name="card">The intro as <see cref="IntroVideo"/> draws it.</param>
    public static IReadOnlyList<string> SwarmIntroVideo(BookendLook look, string card, ExportPreset swarmSize, TimeSpan length, ExportPreset preset, string output)
    {
        var (w, h, fps) = preset;
        string[] graph =
        [
            $"[1:v]format=yuva420p,fade=t=in:st={Seconds(Synth.Hit)}:d=0.6:alpha=1[card]",
            $"[0:v]fade=t=in:st={Seconds(Synth.Hit - TimeSpan.FromSeconds(1))}:d=1[ground]",
            "[ground][card]overlay=shortest=1,format=gbrp[base]",
            $"[2:v]scale={w}:{h}:flags=bicubic,format=gbrp,split[dots][halo]",
            $"[halo]gblur=sigma={Math.Max(2, h / 120)}[glow]",
            "[dots][glow]blend=all_mode=addition:all_opacity=0.7[light]",
            // Screened, not added: light over the bright background brightens it without burning it out to white.
            $"[base][light]blend=all_mode=screen:shortest=1,format=yuv420p,noise=alls=5:allf=t{Finish(length, null)}[v]",
        ];
        return
        [
            "-f", "lavfi", "-i", Gradient(look, preset, length), "-i", card,
            "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{swarmSize.Width}x{swarmSize.Height}", "-r", fps.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0",
            "-filter_complex", string.Join(';', graph), "-map", "[v]", "-t", Seconds(length), "-an", .. Encode(preset), "-y", output,
        ];
    }

    // The slow gradient every bookend is drawn on, in the accent colour.
    private static string Gradient(BookendLook look, ExportPreset preset, TimeSpan length) =>
        $"gradients=s={preset.Width}x{preset.Height}:r={preset.Fps}:c0=0x101828:c1=0x{look.Accent.TrimStart('#')}:x0=0:y0={preset.Height}:x1={preset.Width}:y1=0:speed=0.002:d={Seconds(length)}";

    /// <summary>
    /// The series outro: the channel's name and a call to subscribe across the top, the mascot on the left, and two
    /// faint frames on the right where YouTube's end-screen cards (subscribe, next video) are placed when uploading.
    /// </summary>
    public static IReadOnlyList<string> OutroVideo(BookendLook look, string channelFile, string? nextFile, TimeSpan length, ExportPreset preset, string output) =>
        Card(look, length, preset, output,
        [
            Words(channelFile, "h/12", "(w-text_w)/2", "h*0.08", 0.3),
            "drawtext=text='Subscribe for the next lesson':expansion=none:" + TextStyle("h/22", "(w-text_w)/2", "h*0.20", 0.6),
            .. nextFile is null ? [] : new[] { Words(nextFile, "h/20", "(w-text_w)/2", "h*0.86", 0.9) },
            "drawbox=x=iw*0.50:y=ih*0.36:w=iw*0.21:h=iw*0.21*9/16:color=white@0.35:t=4",
            "drawbox=x=iw*0.74:y=ih*0.36:w=iw*0.21:h=iw*0.21*9/16:color=white@0.35:t=4",
        ], mascotLeft: true);

    // The background, logo and mascot every bookend shares, with its own words drawn over them and the same fades
    // and encoding as a clip, so it joins the clips by copying.
    private static IReadOnlyList<string> Card(BookendLook look, TimeSpan length, ExportPreset preset, string output, IReadOnlyList<string> words, bool mascotLeft = false, bool black = false)
    {
        var (w, h, fps) = preset;
        var inputs = new List<string> { "-f", "lavfi", "-i", black ? $"color=c=black:s={w}x{h}:r={fps}:d={Seconds(length)}" : Gradient(look, preset, length) };
        var graph = new List<string>();
        var picture = "[0:v]";
        if (look.Mascot is { } mascot)
        {
            // Large and to one side, or smaller and above the words in an intro laid out about its centre.
            var (height, x, y) = !mascotLeft && look.Layout == IntroLayout.Centre ? (h * 2 / 5, "(W-w)/2", "H*0.08") : (h * 7 / 10, mascotLeft ? "W*0.05" : "W-w-W*0.05", "(H-h)/2");
            inputs.AddRange(["-loop", "1", "-i", mascot]);
            graph.Add($"[{inputs.Count(a => a == "-i") - 1}:v]scale=-2:{height},format=rgba,fade=t=in:st=0.4:d=0.6:alpha=1[mascot]");
            graph.Add($"{picture}[mascot]overlay=x={x}:y={y}[m]");
            picture = "[m]";
        }
        if (look.Logo is { } logo)
        {
            inputs.AddRange(["-loop", "1", "-i", logo]);
            graph.Add($"[{inputs.Count(a => a == "-i") - 1}:v]scale=-2:{h / 8},format=rgba,fade=t=in:st=0.2:d=0.6:alpha=1[logo]");
            graph.Add($"{picture}[logo]overlay=x={(mascotLeft ? "W-w-W*0.04" : "W*0.04")}:y=H*0.06[l]"); // in the corner away from the mascot
            picture = "[l]";
        }
        graph.Add($"{picture}{string.Join(',', words)}{Finish(length, null)}[v]");
        return [.. inputs, "-filter_complex", string.Join(';', graph), "-map", "[v]", "-t", Seconds(length), "-an", .. Encode(preset), "-y", output];
    }

    // One line of a bookend's words, read from a file in the working folder, fading in at its moment.
    private static string Words(string file, string size, string x, string y, double appears) =>
        $"drawtext=textfile={file}:expansion=none:" + TextStyle(size, x, y, appears);

    private static string TextStyle(string size, string x, string y, double appears) =>
        $"fontfile={TitleFontFile}:fontcolor=white:fontsize={size}:x={x}:y={y}:borderw=3:bordercolor=black@0.35:" +
        FormattableString.Invariant($"alpha='min(1,max(0,(t-{appears:0.0})/0.5))'");
}
