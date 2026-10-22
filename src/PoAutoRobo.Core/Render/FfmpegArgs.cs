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

    public bool Portrait => Height > Width;
}

/// <summary>One moment of a teaser as FFmpeg cuts it: a stretch of a finished clip picture and of that clip's sound.</summary>
public sealed record TeaserCut(string Picture, string Sound, TimeSpan Start, TimeSpan Length);

/// <summary>How the series intro and outro look: the accent colour (#RRGGBB) and the kit's logo and mascot pictures, each null when there is none.</summary>
public sealed record BookendLook(string Accent, string? Logo, string? Mascot);

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
    public const double TargetLufs = -16;

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
    public static IReadOnlyList<string> ClipVideo(ClipSource source, string input, TimeSpan length, ExportPreset preset, string output, string? captionsFile = null, int move = 0)
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
                $"tpad=stop_mode=clone:stop_duration={Seconds(length)}",
            // ponytail: one centred line, no wrapping. Clip titles are short; wrap here if they stop being so.
            _ => $"drawtext=textfile={input}:expansion=none:fontfile={TitleFontFile}:fontcolor=white:fontsize={(preset.Portrait ? "w" : "h")}/12:x=(w-text_w)/2:y=(h-text_h)/2",
        };
        return [.. inputArgs, "-vf", filter + Finish(length, captionsFile), "-t", Seconds(length), "-an", .. Encode(preset), "-y", output];
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
    public static IReadOnlyList<string> IntroVideo(BookendLook look, string channelFile, string? taglineFile, string titleFile, TimeSpan length, ExportPreset preset, string output)
    {
        // With a mascot on the right, the words keep to the left; without one, they are centred.
        var x = look.Mascot is null ? "(w-text_w)/2" : "w*0.08";
        return Card(look, length, preset, output,
        [
            Words(channelFile, "h/10", x, "h*0.34", 0.8),
            .. taglineFile is null ? [] : new[] { Words(taglineFile, "h/24", x, "h*0.48", 1.2) },
            Words(titleFile, "h/14", x, "h*0.60", 1.6),
        ]);
    }

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
    private static IReadOnlyList<string> Card(BookendLook look, TimeSpan length, ExportPreset preset, string output, IReadOnlyList<string> words, bool mascotLeft = false)
    {
        var (w, h, fps) = preset;
        var accent = "0x" + look.Accent.TrimStart('#');
        var inputs = new List<string> { "-f", "lavfi", "-i", $"gradients=s={w}x{h}:r={fps}:c0=0x101828:c1={accent}:x0=0:y0={h}:x1={w}:y1=0:speed=0.002:d={Seconds(length)}" };
        var graph = new List<string>();
        var picture = "[0:v]";
        if (look.Mascot is { } mascot)
        {
            inputs.AddRange(["-loop", "1", "-i", mascot]);
            graph.Add($"[{inputs.Count(a => a == "-i") - 1}:v]scale=-2:{h * 7 / 10},format=rgba,fade=t=in:st=0.4:d=0.6:alpha=1[mascot]");
            graph.Add($"{picture}[mascot]overlay=x={(mascotLeft ? "W*0.05" : "W-w-W*0.05")}:y=(H-h)/2[m]");
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
    /// over a blurred, enlarged copy of itself, the hook across the top, captions, and the channel's name in a bar at
    /// the foot. Its sound is levelled like the master's.
    /// </summary>
    /// <param name="hookFile">Text files in the working folder, as for a title card: the words are drawn, never run.</param>
    public static IReadOnlyList<string> TeaserVideo(IReadOnlyList<TeaserCut> cuts, string hookFile, string? channelFile, string? captionsFile, string output)
    {
        var (w, h, fps) = ExportPreset.Upright;
        var length = TimeSpan.FromTicks(cuts.Sum(c => c.Length.Ticks));
        var graph = new List<string>
        {
            $"{string.Concat(cuts.Select((_, i) => $"[{2 * i}:v][{2 * i + 1}:a]"))}concat=n={cuts.Count}:v=1:a=1[cv][ca]",
            $"[cv]fps={fps},split[back][front]",
            $"[back]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},boxblur=24:2[bg]",
            $"[front]scale={w}:-2[fg]",
            "[bg][fg]overlay=(W-w)/2:(H-h)/2" +
                (captionsFile is null ? "" : $",ass={captionsFile}") +
                $",drawtext=textfile={hookFile}:expansion=none:fontfile={TitleFontFile}:fontcolor=white:fontsize=w/15:line_spacing=14:x=(w-text_w)/2:y=h*0.09:box=1:boxcolor=black@0.5:boxborderw=28" +
                (channelFile is null ? "" : $",drawbox=x=0:y=ih-150:w=iw:h=150:color=black@0.55:t=fill,drawtext=textfile={channelFile}:expansion=none:fontfile={TitleFontFile}:fontcolor=white:fontsize=48:x=(w-text_w)/2:y=h-102") +
                $",fade=t=in:st=0:d={Seconds(Fade)},fade=t=out:st={Seconds(length - Fade)}:d={Seconds(Fade)},format=yuv420p[v]",
            $"[ca]loudnorm=I={TargetLufs.ToString(CultureInfo.InvariantCulture)}:TP=-1.5:LRA=11,aresample=48000[aout]",
        };
        return
        [
            .. cuts.SelectMany(c => new[] { "-ss", Seconds(c.Start), "-t", Seconds(c.Length), "-i", c.Picture, "-ss", Seconds(c.Start), "-t", Seconds(c.Length), "-i", c.Sound }),
            "-filter_complex", string.Join(';', graph), "-map", "[v]", "-map", "[aout]",
            "-t", Seconds(length), .. Encode(ExportPreset.Upright), "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-y", output,
        ];
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
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
        "-r", preset.Fps.ToString(CultureInfo.InvariantCulture), "-video_track_timescale", "90000",
    ];

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

    /// <summary>The text of the list file <see cref="Join"/> reads: one finished clip per line, in running order.</summary>
    public static string ClipList(IEnumerable<string> clips) =>
        string.Concat(clips.Select(path => $"file '{path.Replace("'", @"'\''")}'\n"));

    /// <summary>How loud the music is before the voice pushes it down further: a quarter of its own level.</summary>
    public const double MusicLevel = 0.25;

    /// <summary>Joins the finished clips by copying their pictures, and lays the crossfaded, levelled narration under them.</summary>
    /// <param name="music">A track to play quietly underneath, repeated to the video's length and dipped whenever the voice speaks; null for none.</param>
    public static IReadOnlyList<string> Join(string clipListFile, IReadOnlyList<string> audios, string output, string? music = null)
    {
        var graph = new List<string>();
        for (var i = 0; i < audios.Count; i++)
            graph.Add($"[{i + 1}:a]aformat=sample_rates=48000:channel_layouts=stereo[s{i}]");
        var sound = "[s0]";
        for (var i = 1; i < audios.Count; i++)
        {
            graph.Add($"{sound}[s{i}]acrossfade=d={Seconds(AudioCrossfade)}[a{i}]");
            sound = $"[a{i}]";
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
            "-filter_complex", string.Join(';', graph),
            "-map", "0:v", "-map", "[aout]",
            "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-y", output,
        ];
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
}
