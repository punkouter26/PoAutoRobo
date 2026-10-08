using System.Globalization;

namespace PoAutoRobo.Core.Pipeline;

public sealed record ExportPreset(int Width, int Height, int Fps)
{
    public static readonly ExportPreset Hd30 = new(1920, 1080, 30);
    public static readonly ExportPreset Hd60 = new(1920, 1080, 60);
    public static readonly ExportPreset Uhd30 = new(3840, 2160, 30);
    public static readonly ExportPreset Uhd60 = new(3840, 2160, 60);
}

public enum ClipSource { TitleCard, Image, Video }

/// <summary>Where a clip sits on the episode timeline, and how long its picture must run to cover the dissolve.</summary>
public sealed record Slot(TimeSpan Start, TimeSpan VideoLength);

/// <summary>Builds FFmpeg command lines. Pure functions, so the exact arguments are snapshot-tested.</summary>
public static class FfmpegArgs
{
    public static readonly TimeSpan AudioCrossfade = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan Dissolve = TimeSpan.FromMilliseconds(500);
    public const double TargetLufs = -16;

    /// <summary>Font the title card reads from the working folder. This FFmpeg build has no font lookup by name.</summary>
    public const string TitleFontFile = "title-font.ttf";

    /// <summary>
    /// Narration drives the timeline. Each join overlaps the audio by <see cref="AudioCrossfade"/>, and every picture
    /// but the last runs <see cref="Dissolve"/> longer so it can fade into the next without shifting sync.
    /// </summary>
    public static IReadOnlyList<Slot> Timeline(IReadOnlyList<TimeSpan> narration)
    {
        var slots = new List<Slot>(narration.Count);
        var start = TimeSpan.Zero;
        for (var i = 0; i < narration.Count; i++)
        {
            var last = i == narration.Count - 1;
            var share = last ? narration[i] : narration[i] - AudioCrossfade;
            slots.Add(new Slot(start, last ? share : share + Dissolve));
            start += share;
        }
        return slots;
    }

    /// <summary>Renders one clip's picture (no sound) at the export size and frame rate.</summary>
    /// <param name="input">Image or video path, or for a title card the text file holding the title.</param>
    public static IReadOnlyList<string> ClipVideo(ClipSource source, string input, TimeSpan length, ExportPreset preset, string output)
    {
        var (w, h, fps) = preset;
        var frames = (int)Math.Round(length.TotalSeconds * fps);
        string[] inputArgs = source == ClipSource.TitleCard
            ? ["-f", "lavfi", "-i", $"color=c=0x101828:s={w}x{h}:r={fps}"]
            : ["-i", input];
        var filter = source switch
        {
            // Oversampling before zoompan avoids the visible stair-stepping it has at native size.
            ClipSource.Image =>
                $"scale={2 * w}:{2 * h}:force_original_aspect_ratio=increase,crop={2 * w}:{2 * h}," +
                $"zoompan=z='1+0.08*on/{frames}':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d={frames}:s={w}x{h}:fps={fps},format=yuv420p",
            ClipSource.Video =>
                $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,fps={fps}," +
                $"tpad=stop_mode=clone:stop_duration={Seconds(length)},format=yuv420p",
            // ponytail: one centred line, no wrapping. Clip titles are short; wrap here if they stop being so.
            _ => $"drawtext=textfile={input}:fontfile={TitleFontFile}:fontcolor=white:fontsize=h/12:x=(w-text_w)/2:y=(h-text_h)/2,format=yuv420p",
        };
        return [.. inputArgs, "-vf", filter, "-t", Seconds(length), "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-y", output];
    }

    /// <summary>Renders a panel sequence: each picture gets an equal share of the clip, with its own pan and zoom.</summary>
    public static IReadOnlyList<string> PanelsVideo(IReadOnlyList<string> images, TimeSpan length, ExportPreset preset, string output)
    {
        var (w, h, fps) = preset;
        var total = (int)Math.Round(length.TotalSeconds * fps);
        var graph = new List<string>();
        for (var i = 0; i < images.Count; i++)
        {
            var frames = total / images.Count + (i < total % images.Count ? 1 : 0); // spare frames go to the first panels
            graph.Add(
                $"[{i}:v]scale={2 * w}:{2 * h}:force_original_aspect_ratio=increase,crop={2 * w}:{2 * h}," +
                $"zoompan=z='1+0.08*on/{frames}':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d={frames}:s={w}x{h}:fps={fps}[p{i}]");
        }
        graph.Add($"{string.Concat(images.Select((_, i) => $"[p{i}]"))}concat=n={images.Count}:v=1:a=0,format=yuv420p[v]");
        return
        [
            .. images.SelectMany(path => new[] { "-i", path }),
            "-filter_complex", string.Join(';', graph), "-map", "[v]",
            "-t", Seconds(length), "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-y", output,
        ];
    }

    /// <summary>Joins the clip pictures and narration into the finished file.</summary>
    public static IReadOnlyList<string> Master(
        IReadOnlyList<string> videos, IReadOnlyList<string> audios, IReadOnlyList<Slot> slots,
        string? captionsFile, ExportPreset preset, string output)
    {
        var n = videos.Count;
        var graph = new List<string>();

        var picture = "[0:v]";
        for (var i = 1; i < n; i++)
        {
            graph.Add($"{picture}[{i}:v]xfade=transition=fade:duration={Seconds(Dissolve)}:offset={Seconds(slots[i].Start)}[v{i}]");
            picture = $"[v{i}]";
        }
        graph.Add($"{picture}{(captionsFile is null ? "null" : $"ass={captionsFile}")}[vout]");

        for (var i = 0; i < n; i++)
            graph.Add($"[{n + i}:a]aformat=sample_rates=48000:channel_layouts=stereo[s{i}]");
        var sound = "[s0]";
        for (var i = 1; i < n; i++)
        {
            graph.Add($"{sound}[s{i}]acrossfade=d={Seconds(AudioCrossfade)}[a{i}]");
            sound = $"[a{i}]";
        }
        graph.Add($"{sound}loudnorm=I={TargetLufs.ToString(CultureInfo.InvariantCulture)}:TP=-1.5:LRA=11,aresample=48000[aout]");

        // ponytail: software x264 only. Add a hardware encoder choice if 4K60 exports are too slow.
        return
        [
            .. videos.Concat(audios).SelectMany(path => new[] { "-i", path }),
            "-filter_complex", string.Join(';', graph),
            "-map", "[vout]", "-map", "[aout]",
            "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-r", preset.Fps.ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-y", output,
        ];
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
}
