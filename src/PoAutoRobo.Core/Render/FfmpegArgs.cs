using System.Globalization;

namespace PoAutoRobo.Core.Render;

public sealed record ExportPreset(int Width, int Height, int Fps)
{
    public static readonly ExportPreset Hd30 = new(1920, 1080, 30);
    public static readonly ExportPreset Hd60 = new(1920, 1080, 60);
    public static readonly ExportPreset Uhd30 = new(3840, 2160, 30);
    public static readonly ExportPreset Uhd60 = new(3840, 2160, 60);

    /// <summary>Upright, for teasers on phone-first video sites.</summary>
    public static readonly ExportPreset Upright = new(1080, 1920, 30);

    /// <summary>A teaser small enough to draw in a moment, to watch on its card before rendering it in full.</summary>
    public static readonly ExportPreset UprightDraft = new(360, 640, 30);

    public bool Portrait => Height > Width;
}

/// <summary>One moment of a teaser as FFmpeg cuts it: a stretch of a finished clip picture and of that clip's sound.</summary>
public sealed record TeaserCut(string Picture, string Sound, TimeSpan Start, TimeSpan Length);

/// <summary>
/// How the series intro and outro look: the accent colour (#RRGGBB), the kit's logo and mascot pictures (each null when
/// there is none), and where the intro's words and mascot sit.
/// </summary>
public sealed record BookendLook(string Accent, string? Logo, string? Mascot, IntroLayout Layout = IntroLayout.Side);

/// <summary>
/// The owner's film as the robot's own camera might see it: brightness read as depth in false colour, edges traced in
/// light, scan lines, and a readout naming the moment. A film from a beat where it went wrong also glitches.
/// </summary>
/// <param name="LabelFile">A text file in the working folder with the readout's words; they are drawn, never run.</param>
public sealed record RobotEye(string LabelFile, bool Fault);

/// <summary>Video holds its last frame when the narration runs longer; Loop, for short generated footage, plays again from the start.</summary>
public enum ClipSource { TitleCard, Image, Video, Loop }

/// <summary>Where a clip sits on the episode timeline, and how long its picture runs.</summary>
public sealed record Slot(TimeSpan Start, TimeSpan VideoLength);

/// <summary>
/// Builds FFmpeg command lines. Pure functions, so the exact arguments are snapshot-tested.
/// Each clip is encoded once, complete with its captions and fades, and the finished clips are then joined by
/// copying. Encoding the whole episode a second time to join it took several times longer than everything else.
/// </summary>
public static class FfmpegArgs
{
    public static readonly TimeSpan AudioCrossfade = TimeSpan.FromMilliseconds(150);

    /// <summary>Each clip fades up from black and back down over this long, which reads as a dip between clips.</summary>
    public static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(250);
    private const double TargetLufs = -16;

    /// <summary>Font the title card reads from the working folder. This FFmpeg build has no font lookup by name.</summary>
    public const string TitleFontFile = "title-font.ttf";

    /// <summary>
    /// Narration drives the timeline. Each join overlaps the audio by <see cref="AudioCrossfade"/>, and each picture
    /// runs exactly its share, so the pictures laid end to end stay in step with the sound.
    /// </summary>
    public static IReadOnlyList<Slot> Timeline(IReadOnlyList<TimeSpan> narration)
    {
        var slots = new List<Slot>(narration.Count);
        var start = TimeSpan.Zero;
        for (var i = 0; i < narration.Count; i++)
        {
            var share = i == narration.Count - 1 ? narration[i] : narration[i] - AudioCrossfade;
            slots.Add(new Slot(start, share));
            start += share;
        }
        return slots;
    }

    /// <summary>Renders one finished clip picture (no sound): sized, faded, and with its captions burned in.</summary>
    /// <param name="input">Image or video path, or for a title card the text file holding the title.</param>
    /// <param name="captionsFile">Caption file timed from the start of this clip, read from the working folder; null for none.</param>
    /// <param name="move">Which camera move a still picture gets; see <see cref="PanZoom"/>.</param>
    /// <param name="eye">For footage, the robot's-eye look; null to show it as filmed.</param>
    public static IReadOnlyList<string> ClipVideo(ClipSource source, string input, TimeSpan length, ExportPreset preset, string output, string? captionsFile = null, int move = 0,
        RobotEye? eye = null)
    {
        var (w, h, fps) = preset;
        var frames = (int)Math.Round(length.TotalSeconds * fps);
        string[] inputArgs = source switch
        {
            ClipSource.TitleCard => ["-f", "lavfi", "-i", $"color=c=0x101828:s={w}x{h}:r={fps}"],
            ClipSource.Loop => ["-stream_loop", "-1", "-i", input],
            _ => ["-i", input],
        };
        var filter = source switch
        {
            ClipSource.Image => PanZoom(preset, frames, move),
            ClipSource.Video or ClipSource.Loop =>
                $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,fps={fps}," +
                $"tpad=stop_mode=clone:stop_duration={Seconds(length)}" + (eye is null ? "" : RobotLook(eye)),
            // ponytail: one centred line, no wrapping. Clip titles are short; wrap here if they stop being so.
            _ => $"drawtext=textfile={input}:expansion=none:fontfile={TitleFontFile}:fontcolor=white:fontsize={(preset.Portrait ? "w" : "h")}/12:x=(w-text_w)/2:y=(h-text_h)/2",
        };
        return [.. inputArgs, "-vf", filter + Finish(length, captionsFile), "-t", Seconds(length), "-an", .. Encode(preset), "-y", output];
    }

    // The robot's-eye look, after the film is fitted to the frame. Brightness stands in for depth: there is no depth
    // camera's data in an ordinary film, and near things are usually the lit ones.
    private static string RobotLook(RobotEye eye)
    {
        const string Cyan = "0x00E5FF";
        var hud = $"fontfile={TitleFontFile}:fontsize=h/26:";
        // Four corner brackets, each two short bars, framing the view as a camera's readout does.
        (string X, string Y, bool Right, bool Bottom)[] corners = [("0.04", "0.06", false, false), ("0.96", "0.06", true, false), ("0.04", "0.94", false, true), ("0.96", "0.94", true, true)];
        var brackets = string.Concat(corners.Select(c =>
            $",drawbox=x=iw*{c.X}{(c.Right ? "-iw*0.06" : "")}:y=ih*{c.Y}{(c.Bottom ? "-3" : "")}:w=iw*0.06:h=3:color={Cyan}@0.8:t=fill" +
            $",drawbox=x=iw*{c.X}{(c.Right ? "-3" : "")}:y=ih*{c.Y}{(c.Bottom ? "-ih*0.08" : "")}:w=3:h=ih*0.08:color={Cyan}@0.8:t=fill"));
        return
            ",split[cam][edges];" +
            "[cam]format=gray,format=yuv444p,pseudocolor=preset=turbo,eq=brightness=-0.08:saturation=0.8,format=gbrp[depth];" +
            "[edges]edgedetect=low=0.08:high=0.22,format=gbrp[lines];" +
            "[depth][lines]blend=all_mode=screen,drawgrid=w=iw:h=4:t=1:c=black@0.25,vignette=PI/5" +
            (eye.Fault ? ",rgbashift=rh=-6:bh=6,noise=alls=18:allf=t" : ",noise=alls=6:allf=t") +
            brackets +
            $",drawtext=text='R1 VISION':expansion=none:{hud}fontcolor={Cyan}:x=w*0.06:y=h*0.08" +
            $",drawtext=textfile={eye.LabelFile}:expansion=none:{hud}fontcolor=white:x=w*0.06:y=h*0.13" +
            $",drawtext=text='%{{pts\\:hms}}':{hud}fontcolor={Cyan}:x=w*0.94-text_w:y=h*0.08" + // the clock is the app's own words, so it may be run
            ",drawbox=x=iw*0.94-ih*0.03:y=ih*0.14:w=ih*0.03:h=ih*0.03:color=red@0.9:t=fill:enable='lt(mod(t,1),0.5)'" +
            (eye.Fault ? $",drawtext=text='FAULT':expansion=none:{hud}fontcolor=red:x=w*0.94-text_w:y=h*0.19:enable='lt(mod(t,0.5),0.3)'" : "");
    }

    /// <summary>Renders a panel sequence: each picture gets an equal share of the clip, with its own pan and zoom.</summary>
    public static IReadOnlyList<string> PanelsVideo(IReadOnlyList<string> images, TimeSpan length, ExportPreset preset, string output, string? captionsFile = null)
    {
        var (w, h, fps) = preset;
        var total = (int)Math.Round(length.TotalSeconds * fps);
        var graph = new List<string>();
        for (var i = 0; i < images.Count; i++)
        {
            var frames = total / images.Count + (i < total % images.Count ? 1 : 0); // spare frames go to the first panels
            graph.Add($"[{i}:v]{PanZoom(preset, frames)}[p{i}]");
        }
        graph.Add($"{string.Concat(images.Select((_, i) => $"[p{i}]"))}concat=n={images.Count}:v=1:a=0{Finish(length, captionsFile)}[v]");
        return
        [
            .. images.SelectMany(path => new[] { "-i", path }),
            "-filter_complex", string.Join(';', graph), "-map", "[v]",
            "-t", Seconds(length), "-an", .. Encode(preset), "-y", output,
        ];
    }

    /// <summary>
    /// Renders a layered picture: the background pushes in slowly while the subject, cut out on a transparent
    /// background and a little larger than the frame, slides across in front of it.
    /// </summary>
    public static IReadOnlyList<string> ParallaxVideo(string background, string subject, TimeSpan length, ExportPreset preset, string output, string? captionsFile = null)
    {
        var (w, h, fps) = preset;
        var frames = (int)Math.Round(length.TotalSeconds * fps);
        var (fw, fh) = (w * 106 / 100 / 2 * 2, h * 106 / 100 / 2 * 2); // even numbers, as the encoder needs
        var graph =
            $"[0:v]{PanZoom(preset, frames)}[bg];" +
            $"[1:v]format=rgba,scale={fw}:{fh}:force_original_aspect_ratio=increase,crop={fw}:{fh}[fg];" +
            $"[bg][fg]overlay=x='-(w-W)*t/{Seconds(length)}':y='-(h-H)/2'{Finish(length, captionsFile)}[v]";
        return
        [
            "-i", background, "-i", subject,
            "-filter_complex", graph, "-map", "[v]",
            "-t", Seconds(length), "-an", .. Encode(preset), "-y", output,
        ];
    }

    /// <summary>
    /// A slow camera move on a still picture: 0 pushes in, 1 pulls back, 2 and 3 drift right and left. Oversampling
    /// before zoompan avoids the stair-stepping it shows at native size.
    /// </summary>
    private static string PanZoom(ExportPreset p, int frames, int move = 0)
    {
        const string Centre = "iw/2-(iw/zoom/2)";
        var (zoom, x) = move switch
        {
            1 => ($"1.08-0.08*on/{frames}", Centre),
            2 => ("1.08", $"(iw-iw/zoom)*on/{frames}"),
            3 => ("1.08", $"(iw-iw/zoom)*(1-on/{frames})"),
            _ => ($"1+0.08*on/{frames}", Centre),
        };
        return $"scale={2 * p.Width}:{2 * p.Height}:force_original_aspect_ratio=increase,crop={2 * p.Width}:{2 * p.Height}," +
            $"zoompan=z='{zoom}':x='{x}':y='ih/2-(ih/zoom/2)':d={frames}:s={p.Width}x{p.Height}:fps={p.Fps}";
    }

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

    /// <summary>
    /// An upright teaser: its moments cut from the finished clips and their sound and joined, the 16:9 picture centred
    /// over a blurred, enlarged copy of itself (or cropped to fill the frame), the hook across the top, captions, and
    /// the channel's name in a bar at the foot. Its sound is levelled like the master's.
    /// </summary>
    /// <param name="hookFiles">The hook's lines (see <see cref="HookLines"/>), a text file each in the working folder, as for a title card: the words are drawn, never run.</param>
    /// <param name="fill">Crop the picture to fill the frame instead of showing it whole over a blurred copy.</param>
    /// <param name="size">A smaller upright frame for a quick draft to watch; the full 1080 by 1920 when null.</param>
    public static IReadOnlyList<string> TeaserVideo(IReadOnlyList<TeaserCut> cuts, IReadOnlyList<string> hookFiles, string? channelFile, string? captionsFile, string output,
        bool fill = false, ExportPreset? size = null)
    {
        var preset = size ?? ExportPreset.Upright;
        var (w, h, fps) = preset;
        int Px(int full) => full * h / ExportPreset.Upright.Height; // sizes are set for the full frame and shrink with a draft's
        var length = TimeSpan.FromTicks(cuts.Sum(c => c.Length.Ticks));
        var graph = new List<string> { $"{string.Concat(cuts.Select((_, i) => $"[{2 * i}:v][{2 * i + 1}:a]"))}concat=n={cuts.Count}:v=1:a=1[cv][ca]" };
        string framed;
        if (fill)
        {
            framed = $"[cv]fps={fps},scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}";
        }
        else
        {
            graph.Add($"[cv]fps={fps},split[back][front]");
            graph.Add($"[back]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},boxblur={Px(24)}:2[bg]");
            graph.Add($"[front]scale={w}:-2[fg]");
            framed = "[bg][fg]overlay=(W-w)/2:(H-h)/2";
        }
        graph.Add(framed +
            (captionsFile is null ? "" : $",ass={captionsFile}") +
            string.Concat(hookFiles.Select((file, i) => $",drawtext=textfile={file}:expansion=none:fontfile={TitleFontFile}:fontcolor=white:fontsize=w/15:x=(w-text_w)/2:" +
                $"{Drop(i, $"h*0.09+{i * Px(150)}", "h*0.12")}:box=1:boxcolor=black@0.5:boxborderw={Px(28)}")) +
            (channelFile is null ? "" : $",drawbox=x=0:y=ih-{Px(150)}:w=iw:h={Px(150)}:color=black@0.55:t=fill,drawtext=textfile={channelFile}:expansion=none:fontfile={TitleFontFile}:fontcolor=white:fontsize={Px(48)}:x=(w-text_w)/2:y=h-{Px(102)}") +
            $",fade=t=in:st=0:d={Seconds(Fade)},fade=t=out:st={Seconds(length - Fade)}:d={Seconds(Fade)},format=yuv420p[v]");
        graph.Add($"[ca]loudnorm=I={TargetLufs.ToString(CultureInfo.InvariantCulture)}:TP=-1.5:LRA=11,aresample=48000[aout]");
        return
        [
            .. cuts.SelectMany(c => new[] { "-ss", Seconds(c.Start), "-t", Seconds(c.Length), "-i", c.Picture, "-ss", Seconds(c.Start), "-t", Seconds(c.Length), "-i", c.Sound }),
            "-filter_complex", string.Join(';', graph), "-map", "[v]", "-map", "[aout]",
            "-t", Seconds(length), .. Encode(preset), "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-y", output,
        ];
    }

    /// <summary>
    /// Where line <paramref name="line"/> of a teaser's hook is drawn: each falls from <paramref name="height"/> above its
    /// place at <paramref name="rest"/>, a moment after the line before, and bounces to rest as a dropped thing does,
    /// each bounce lower and quicker than the last.
    /// </summary>
    private static string Drop(int line, string rest, string height)
    {
        var at = FormattableString.Invariant($"(t-{0.15 + 0.12 * line:0.00})");
        // The bounces are a fading wave folded upwards: |cos| draws the arcs, and the falling e makes each one lower.
        return $"y='{rest}-{height}*exp(-5*max(0,{at}))*abs(cos(9*max(0,{at})))':alpha='min(1,max(0,{at}/0.08+1))'";
    }

    /// <summary>A hook broken into lines of at most four words, so it fits across the narrow frame.</summary>
    public static string HookLines(string hook) =>
        string.Join('\n', Durations.SplitWords(hook).Chunk(4).Select(line => string.Join(' ', line)));

    /// <summary>The last filters on every clip: captions over the picture, then the fades over both.</summary>
    private static string Finish(TimeSpan length, string? captionsFile) =>
        (captionsFile is null ? "" : $",ass={captionsFile}") +
        $",fade=t=in:st=0:d={Seconds(Fade)},fade=t=out:st={Seconds(length - Fade)}:d={Seconds(Fade)},format=yuv420p";

    // Identical for every clip: the join copies the pictures, which only works when they are encoded the same way.
    // ponytail: software x264 only. A native (not emulated) FFmpeg build is the bigger win on ARM machines.
    private static string[] Encode(ExportPreset preset) =>
    [
        .. Compression,
        "-r", preset.Fps.ToString(CultureInfo.InvariantCulture), "-video_track_timescale", "90000",
    ];

    /// <summary>How every picture the app makes is compressed. A clip's own frames use it too, before the clip is encoded.</summary>
    public static IReadOnlyList<string> Compression { get; } = ["-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p"];

    /// <summary>A picture and its sound in one file: the picture copied as it is, the sound made AAC, ending with the shorter.</summary>
    public static IReadOnlyList<string> Mux(string picture, string sound, string output) =>
        ["-i", picture, "-i", sound, "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-shortest", "-y", output];

    /// <summary>
    /// The sound of the owner's film as a track exactly <paramref name="length"/> long, in the narration's form, so it
    /// takes a narration's place in the timeline. A film with no sound track (null) gives silence of that length.
    /// </summary>
    public static IReadOnlyList<string> FootageSound(string? video, TimeSpan length, string output) =>
    [
        .. video is null
            ? new[] { "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo" }
            : ["-i", video, "-map", "0:a:0", "-af", "aformat=sample_rates=48000:channel_layouts=stereo,apad"], // padded: a film's sound can stop before its picture
        "-vn", "-t", Seconds(length), "-c:a", "pcm_s16le", "-y", output,
    ];

    /// <summary>A film's sound as speech to text reads it best: one channel at 16 kHz.</summary>
    public static IReadOnlyList<string> SpeechSound(string video, string output) =>
        ["-i", video, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-y", output];

    /// <summary>
    /// One frame of a video at <paramref name="at"/>, <paramref name="width"/> pixels wide, or as large as the video
    /// for 0. Sought before the input is opened, so it is quick at any point.
    /// </summary>
    public static IReadOnlyList<string> Still(string video, TimeSpan at, int width, string output) =>
        ["-ss", Seconds(at), "-i", video, "-frames:v", "1", .. width > 0 ? new[] { "-vf", $"scale={width}:-2" } : [], "-y", output];

    /// <summary>The text of the list file <see cref="Join"/> reads: one finished clip per line, in running order.</summary>
    public static string ClipList(IEnumerable<string> clips) =>
        string.Concat(clips.Select(path => $"file '{path.Replace("'", @"'\''")}'\n"));

    /// <summary>How loud the music is before the voice pushes it down further: a quarter of its own level.</summary>
    private const double MusicLevel = 0.25;

    /// <summary>Joins the finished clips by copying their pictures, and lays the crossfaded, levelled narration under them.</summary>
    /// <param name="music">A track to play quietly underneath, repeated to the video's length and dipped whenever the voice speaks; null for none.</param>
    /// <param name="whoosh">A sound (see <see cref="Synth.Whoosh"/>) played into each of the <paramref name="cuts"/>, loudest on the cut; null for none.</param>
    public static IReadOnlyList<string> Join(string clipListFile, IReadOnlyList<string> audios, string output, string? music = null, string? whoosh = null,
        IReadOnlyList<TimeSpan>? cuts = null)
    {
        var starts = whoosh is null ? [] : (cuts ?? []).Select(cut => cut - Synth.WhooshPeak).Where(start => start >= TimeSpan.Zero).ToList();
        var graph = new List<string>();
        for (var i = 0; i < audios.Count; i++)
            graph.Add($"[{i + 1}:a]aformat=sample_rates=48000:channel_layouts=stereo[s{i}]");
        var sound = "[s0]";
        for (var i = 1; i < audios.Count; i++)
        {
            graph.Add($"{sound}[s{i}]acrossfade=d={Seconds(AudioCrossfade)}[a{i}]");
            sound = $"[a{i}]";
        }
        if (starts.Count > 0)
        {
            // One copy of the whoosh for each cut, each held back to its moment and laid over the voice.
            var input = audios.Count + 1 + (music is null ? 0 : 1);
            graph.Add($"[{input}:a]aformat=sample_rates=48000:channel_layouts=stereo,asplit={starts.Count}{string.Concat(starts.Select((_, j) => $"[w{j}]"))}");
            for (var j = 0; j < starts.Count; j++)
                graph.Add(FormattableString.Invariant($"[w{j}]adelay=delays={(long)starts[j].TotalMilliseconds}:all=1[d{j}]"));
            graph.Add($"{sound}{string.Concat(starts.Select((_, j) => $"[d{j}]"))}amix=inputs={starts.Count + 1}:duration=first:normalize=0[swept]");
            sound = "[swept]";
        }
        if (music is not null)
        {
            // The voice is heard twice over: once in the mix, and once as the signal that pushes the music down.
            graph.Add($"{sound}asplit[voice][key]");
            graph.Add($"[{audios.Count + 1}:a]aformat=sample_rates=48000:channel_layouts=stereo,volume={MusicLevel.ToString(CultureInfo.InvariantCulture)}[bed]");
            graph.Add("[bed][key]sidechaincompress=threshold=0.03:ratio=8:attack=20:release=400[ducked]");
            graph.Add("[voice][ducked]amix=inputs=2:duration=first:normalize=0[mixed]"); // ends with the voice, however long the track is
            sound = "[mixed]";
        }
        graph.Add($"{sound}loudnorm=I={TargetLufs.ToString(CultureInfo.InvariantCulture)}:TP=-1.5:LRA=11,aresample=48000[aout]");

        return
        [
            "-f", "concat", "-safe", "0", "-i", clipListFile,
            .. audios.SelectMany(path => new[] { "-i", path }),
            .. music is null ? [] : new[] { "-stream_loop", "-1", "-i", music },
            .. starts.Count == 0 ? [] : new[] { "-i", whoosh! },
            "-filter_complex", string.Join(';', graph),
            "-map", "0:v", "-map", "[aout]",
            "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-y", output,
        ];
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
}
