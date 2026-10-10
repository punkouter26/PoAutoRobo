
namespace PoAutoRobo.Core.Pictures;

/// <summary>One kind of visual: its name in a script, its name on screen, and what the script writer is told about it.</summary>
/// <param name="Brief">When to choose it, and what the clip's visual prompt must then say.</param>
public sealed record KindInfo(VisualKind Kind, string Key, string Label, string Brief);

/// <summary>What a code-drawn scene is asked to show, and for how long.</summary>
public sealed record SceneRequest(VisualKind Kind, string Shows, Look Look, string Narration, TimeSpan Length);

/// <summary>Turns a clip into a picture request and attaches the result to the clip.</summary>
/// <param name="characterSheetPath">The locked host design. Until it exists the host is described in words.</param>
/// <param name="model">Name of the picture model in use; part of the cache key so a model change regenerates.</param>
public sealed class Visuals(ImageMaker images, MediaCache cache, string characterSheetPath, string model)
{
    public const string Style = "Bold, clean comic-book panel in 16:9 with flat colours and thick ink outlines. No speech bubbles, captions or lettering.";

    public const string HostInWords = "The host is a friendly, confident cartoon humanoid robot modelled on the Unitree R1: a slim white and grey body, black joints and a rounded dark visor for a face.";

    /// <summary>
    /// Every kind a clip can be given by the script writer or by hand, in the order the lists show them. The user's own
    /// footage is not here: it is attached, not chosen.
    /// </summary>
    public static readonly IReadOnlyList<KindInfo> Kinds =
    [
        new(VisualKind.Still, "still", "Still picture", "One illustrated picture with a slow camera move. The default. visualPrompt: one sentence describing the picture."),
        new(VisualKind.MultiPanel, "panels", "Panel sequence", "Three separate pictures shown one after another (the setup, the key moment, the result), for a process or a before and after. visualPrompt: one sentence naming the three stages in order; do not mention pictures, panels or a row."),
        new(VisualKind.Parallax, "layered", "Layered picture", "One subject in front of a separate background that drifts behind it, for a sense of depth. visualPrompt: the subject, then the setting behind it."),
        new(VisualKind.Animation, "animation", "Animation", "A simple animated diagram or visual metaphor drawn in code, with short labels. Best for mechanisms, flows and comparisons. visualPrompt: what is on screen and how it moves, in one or two sentences."),
        new(VisualKind.Chart, "chart", "Animated chart", "An animated chart. Only when the dialogue itself states the figures. visualPrompt: the chart type, then every label and value to plot."),
        new(VisualKind.KineticText, "text", "Animated text", "A key phrase or figure animated on screen, for a hook, a quote or a takeaway. visualPrompt: the exact words to show, twelve at most."),
        new(VisualKind.Stock, "photo", "Stock photo", "A real photograph from a stock library, for real places, objects and everyday scenes. visualPrompt: two to four plain search words."),
        new(VisualKind.AiVideo, "video", "AI video", "A few seconds of generated moving footage. Costly: two clips an episode at most, for the moments that matter most. visualPrompt: one sentence describing the shot and how the camera moves."),
        new(VisualKind.TitleCard, "title", "Title card", "The clip's title alone on a plain background, for a chapter break. visualPrompt: the title."),
    ];

    /// <summary>The kind a script named; a title card when it named none or one that does not exist.</summary>
    public static VisualKind KindFor(string? key) => Kinds.FirstOrDefault(k => k.Key == key)?.Kind ?? VisualKind.TitleCard;

    /// <summary>What every still picture in this look is asked to be.</summary>
    public static string StyleOf(Look look) => look switch
    {
        Look.Photoreal => "A realistic photograph in 16:9 with natural light and a shallow depth of field. No captions or lettering.",
        Look.FlatVector => "A clean flat vector illustration in 16:9 with simple shapes and a limited palette. No captions or lettering.",
        Look.Cinematic => "A cinematic, dramatically lit 3D render in 16:9, dark and muted with one glowing accent colour. No captions or lettering.",
        _ => Style,
    };

    /// <summary>The same look in a few words, for footage and code-drawn scenes.</summary>
    public static string LookInWords(Look look) => look switch
    {
        Look.Photoreal => "realistic, with natural light and colour",
        Look.FlatVector => "flat vector shapes in a limited palette, with no outlines",
        Look.Cinematic => "dark and cinematic, muted colours with one glowing accent",
        _ => "bold flat colours with thick dark outlines, like a comic book",
    };

    public bool HasSheet => File.Exists(characterSheetPath);

    /// <summary>Called each time a picture is really made (and so paid for), not when a saved one is reused.</summary>
    public Action? PictureMade { get; init; }

    /// <summary>The same, for an AI video.</summary>
    public Action? VideoMade { get; init; }

    /// <summary>Low for quick, cheap drafts; medium for pictures worth publishing.</summary>
    public Quality Quality { get; init; } = Quality.Medium;

    // The three makers below take what to make and the file to write it to. Each is null when it is not set up, and
    // that kind is then not offered to the script writer.

    /// <summary>Finds a stock photograph for a few search words; the number is which match to take, 0 the best.</summary>
    public Func<string, int, string, CancellationToken, Task>? Stock { get; init; }

    /// <summary>Generates a few seconds of footage from a description.</summary>
    public Func<string, string, CancellationToken, Task>? Video { get; init; }

    /// <summary>Writes a scene in code and renders it to a video as long as the clip.</summary>
    public Func<SceneRequest, string, CancellationToken, Task>? Scene { get; init; }

    /// <summary>Thinks of another picture for a clip whose own was declined; null when there is nobody to ask.</summary>
    public Func<Clip, CancellationToken, Task<SaferPicture>>? Rethink { get; init; }

    /// <summary>
    /// Makes what the clip shows, as <see cref="DrawAsync"/> does. When the picture service will not draw it, the
    /// clip is given a picture it will: first the same idea described as a diagram, then a stock photograph of the
    /// place or object, then a diagram drawn in code, and failing all of those a title card.
    /// </summary>
    /// <returns>
    /// The clip as it was drawn (itself, or with the description or picture type that was used in its place), its
    /// files, and for a clip that was changed a sentence saying how, fit to show.
    /// </returns>
    public async Task<(Clip Drawn, IReadOnlyList<string> Paths, string? Change)> DrawOrSubstituteAsync(Clip clip, Look look, CancellationToken ct)
    {
        try
        {
            return (clip, await DrawAsync(clip, look, ct), null);
        }
        catch (PictureDeclinedException declined) when (Rethink is not null)
        {
            SaferPicture safer;
            try
            {
                safer = await Rethink(clip, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw declined; // nothing better was thought of, so the first answer is the one to give
            }

            var why = $"The picture service would not draw “{clip.Title}” as described{(declined.Flagged is null ? "" : $" ({declined.Flagged})")}";
            (Clip Clip, string Change)[] substitutes =
            [
                (EpisodeEditor.WithVisualPrompt(clip, safer.Diagram), $"{why}, so it was described again as a diagram."),
                (EpisodeEditor.WithVisualPrompt(clip, safer.SearchWords) with { Visual = new VisualSpec(VisualKind.Stock, KindLocked: true) }, $"{why}, so it shows a stock photograph."),
                (EpisodeEditor.WithVisualPrompt(clip, safer.Diagram) with { Visual = new VisualSpec(VisualKind.Animation, KindLocked: true) }, $"{why}, so it shows an animated diagram."),
            ];
            foreach (var (substitute, change) in substitutes.Where(s => CanMake(s.Clip.Visual.Kind)))
            {
                try
                {
                    return (substitute, await DrawAsync(substitute, look, ct), change);
                }
                catch (InvalidOperationException)
                {
                    // Declined as well, or nothing found, or the diagram's code would not run: the next is tried.
                }
            }
            return (clip with { Visual = new VisualSpec(VisualKind.TitleCard, KindLocked: true) }, [], $"{why}, and nothing else could be made in its place, so it shows its title.");
        }
    }

    public bool CanMake(VisualKind kind) => kind switch
    {
        VisualKind.Stock => Stock is not null,
        VisualKind.AiVideo => Video is not null,
        VisualKind.Animation or VisualKind.Chart or VisualKind.KineticText => Scene is not null,
        _ => true,
    };

    // Medium, and the first take, are left out of the name so pictures saved before there was a choice are still
    // found and not paid for again.
    private Task<string> CachedAsync(ImageRequest request, string reference, int take, CancellationToken ct) =>
        cache.GetOrCreateAsync([model, request.Size, request.Prompt, reference, .. request.Quality == Quality.Medium ? [] : new[] { request.Quality.ToString().ToLowerInvariant() }, .. TakeKey(take)], ".png", async scratch =>
        {
            await images(request, scratch, ct);
            PictureMade?.Invoke();
        });

    /// <summary>What a take adds to a saved file's name: nothing for the first, so each later take is a separate file and a fresh request.</summary>
    private static string[] TakeKey(int take) => take == 0 ? [] : [$"take {take}"];

    /// <summary>Makes several different takes on the host for the user to choose between. Nothing is locked yet.</summary>
    public async Task<IReadOnlyList<string>> CandidateSheetsAsync(int count, CancellationToken ct)
    {
        var candidates = new List<string>(count);
        for (var i = 1; i <= count; i++)
        {
            // The take number is part of the prompt, so each is a separate request and a separate cached file.
            var request = new ImageRequest(
                $"{Style} Character design sheet, take {i}: front view, side view and three-quarter view of one character on a plain white background. {HostInWords}", null, Quality: Quality);
            candidates.Add(await CachedAsync(request, "", 0, ct));
        }
        return candidates;
    }

    /// <summary>Makes this picture the host for every episode from now on.</summary>
    public void LockSheet(string path)
    {
        Files.EnsureFolderFor(characterSheetPath);
        File.Copy(path, characterSheetPath, overwrite: true);
    }

    /// <summary>What to ask for. Host-visible clips carry the character sheet; off-screen clips ask for the subject alone.</summary>
    public ImageRequest RequestFor(Clip clip, Look look = Look.Comic)
    {
        var script = clip.Active;
        var style = StyleOf(look);
        if (!clip.HostVisible)
            return new ImageRequest($"{style} A full-frame view of the subject itself, with no characters or robots presenting. {script.VisualPrompt}", null, Quality: Quality);

        var host = HasSheet ? "The host is the robot in the reference image; keep its design, colours and proportions exactly." : HostInWords;
        return new ImageRequest($"{style} {script.VisualPrompt} {host} The host is {script.Pose}. What the host is saying, for context only: {script.Dialogue}", HasSheet ? characterSheetPath : null, Quality: Quality);
    }

    /// <summary>Pictures in a panel sequence.</summary>
    public const int PanelCount = 3;

    private static readonly string[] Beats = ["the setup", "the key moment", "the result"];

    /// <summary>
    /// How many pictures a clip of this kind needs from the picture model: three for a panel sequence, two for a layered
    /// picture, one for a still, none for the rest. The single place that decides it, for costing and for drawing alike.
    /// </summary>
    public static int PictureCount(VisualKind kind) => kind switch
    {
        VisualKind.MultiPanel => PanelCount,
        VisualKind.Parallax => 2,
        VisualKind.Still => 1,
        _ => 0,
    };

    /// <summary>True for the kinds that have something to be made; a title card and the user's own footage have not.</summary>
    public static bool IsGenerated(VisualKind kind) => kind is not (VisualKind.TitleCard or VisualKind.UserVideo);

    /// <summary>
    /// Makes whatever one clip shows and returns its files, reusing saved files for identical requests. The caller
    /// attaches them with <see cref="EpisodeEditor.ApplyPicture"/> against the episode as it is by then.
    /// </summary>
    /// <exception cref="InvalidOperationException">The clip's kind is not set up here; the message says what it needs.</exception>
    public async Task<IReadOnlyList<string>> DrawAsync(Clip clip, Look look, CancellationToken ct)
    {
        var script = clip.Active;
        var take = clip.Visual.Take;
        switch (clip.Visual.Kind)
        {
            case VisualKind.Stock:
                var find = Stock ?? throw new InvalidOperationException("Stock photos need a Pexels key. Add a secret named Pexels--ApiKey to the key vault, or set POAUTOROBO_PEXELS_KEY, and restart.");
                return [await cache.GetOrCreateAsync(["stock", script.VisualPrompt, .. TakeKey(take)], ".jpg", scratch => find(script.VisualPrompt, take, scratch, ct))];

            case VisualKind.AiVideo:
                var film = Video ?? throw new InvalidOperationException("AI video needs a video model. Set POAUTOROBO_VIDEO_MODEL to the name of its deployment (for example sora-2) and restart.");
                // ponytail: described in words only, so the host is not held to its character sheet. Send the sheet as
                // the first frame (resized to the video's size) if the host must look the same in footage.
                var shot = $"{script.VisualPrompt} The look: {LookInWords(look)}. No captions or lettering.";
                return [await cache.GetOrCreateAsync(["video", shot, .. TakeKey(take)], ".mp4", async scratch =>
                {
                    await film(shot, scratch, ct);
                    VideoMade?.Invoke();
                })];

            case VisualKind.Animation or VisualKind.Chart or VisualKind.KineticText:
                var draw = Scene ?? throw new InvalidOperationException("Animations are drawn with Microsoft Edge and FFmpeg, and one of them was not found on this computer.");
                // Whole seconds, from the word count: the real recording may run a little over, and the last frame is then held.
                var length = TimeSpan.FromSeconds(Math.Ceiling(Durations.Estimate(script.Dialogue).TotalSeconds));
                var request = new SceneRequest(clip.Visual.Kind, script.VisualPrompt, look, script.Dialogue, length);
                return [await cache.GetOrCreateAsync(["scene", $"{request.Kind}", $"{look}", request.Shows, request.Narration, .. TakeKey(take)], ".mp4", scratch => draw(request, scratch, ct))];

            case VisualKind.Parallax:
                var style = StyleOf(look);
                return
                [
                    await CachedAsync(new ImageRequest($"{style} The setting alone, with no characters and nothing in the foreground. {script.VisualPrompt}", null, Quality: Quality), "", take, ct),
                    await CachedAsync(new ImageRequest($"{style} The main subject alone, whole and uncropped, on a fully transparent background. {script.VisualPrompt}", null, Quality: Quality, Transparent: true), "", take, ct),
                ];
        }

        var picture = RequestFor(clip, look);
        var reference = picture.ReferencePath is null ? "" : Files.ContentHash(picture.ReferencePath);

        var prompts = clip.Visual.Kind == VisualKind.MultiPanel
            ? Beats.Select((beat, i) => $"{picture.Prompt} Draw one single full-frame picture, never a strip, a grid or several panels: of the stages described, it shows only stage {i + 1} of {PanelCount}, {beat}.")
            : [picture.Prompt];
        var paths = new List<string>();
        foreach (var prompt in prompts)
            paths.Add(await CachedAsync(picture with { Prompt = prompt }, reference, take, ct));
        return paths;
    }
}
