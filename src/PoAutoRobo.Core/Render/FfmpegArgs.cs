using System.Globalization;

namespace PoAutoRobo.Core.Render;

public sealed record ExportPreset(int Width, int Height, int Fps)
{
    public static readonly ExportPreset Hd30 = new(1920, 1080, 30);
    public static readonly ExportPreset Hd60 = new(1920, 1080, 60);
    public static readonly ExportPreset Uhd30 = new(3840, 2160, 30);
    public static readonly ExportPreset Uhd60 = new(3840, 2160, 60);

    /// <summary>Upright, for one clip at a time on phone-first video sites.</summary>
    public static readonly ExportPreset Shorts = new(1080, 1920, 30);

    public bool Portrait => Height > Width;
}

public enum ClipSource { TitleCard, Image, Video }

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
    public static IReadOnlyList<string> ClipVideo(ClipSource source, string input, TimeSpan length, ExportPreset preset, string output, string? captionsFile = null)
    {
        var (w, h, fps) = preset;
        var frames = (int)Math.Round(length.TotalSeconds * fps);
        string[] inputArgs = source == ClipSource.TitleCard
            ? ["-f", "lavfi", "-i", $"color=c=0x101828:s={w}x{h}:r={fps}"]
            : ["-i", input];
        var filter = source switch
        {
            ClipSource.Image => PanZoom(preset, frames),
            ClipSource.Video =>
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

    /// <summary>A slow push-in on a still picture. Oversampling before zoompan avoids the stair-stepping it shows at native size.</summary>
    private static string PanZoom(ExportPreset p, int frames) =>
        $"scale={2 * p.Width}:{2 * p.Height}:force_original_aspect_ratio=increase,crop={2 * p.Width}:{2 * p.Height}," +
        $"zoompan=z='1+0.08*on/{frames}':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d={frames}:s={p.Width}x{p.Height}:fps={p.Fps}";

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

    /// <summary>The text of the list file <see cref="Join"/> reads: one finished clip per line, in running order.</summary>
    public static string ClipList(IEnumerable<string> clips) =>
        string.Concat(clips.Select(path => $"file '{path.Replace("'", @"'\''")}'\n"));

    /// <summary>Joins the finished clips by copying their pictures, and lays the crossfaded, levelled narration under them.</summary>
    public static IReadOnlyList<string> Join(string clipListFile, IReadOnlyList<string> audios, string output)
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
        graph.Add($"{sound}loudnorm=I={TargetLufs.ToString(CultureInfo.InvariantCulture)}:TP=-1.5:LRA=11,aresample=48000[aout]");

        return
        [
            "-f", "concat", "-safe", "0", "-i", clipListFile,
            .. audios.SelectMany(path => new[] { "-i", path }),
            "-filter_complex", string.Join(';', graph),
            "-map", "0:v", "-map", "[aout]",
            "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-y", output,
        ];
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
}
