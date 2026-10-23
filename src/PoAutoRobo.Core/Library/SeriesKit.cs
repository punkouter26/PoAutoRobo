namespace PoAutoRobo.Core.Library;

/// <summary>
/// Where the intro's words and mascot sit. Side: the words on the left, the mascot on the right. Centre: the mascot
/// above, the words centred under it. Lower: the mascot on the right, the words on a dark band across the lower third.
/// </summary>
public enum IntroLayout { Side, Centre, Lower }

/// <summary>
/// What every episode of the series shares: the channel's name, its logo, sounds and mascot for the intro and outro,
/// and the look new episodes start in. Set up once and kept beside the app's settings, never in an episode.
/// </summary>
/// <remarks>
/// The files are the kit's own copies, named by what they are for ("logo.png"), so moving or deleting the file the
/// user picked changes nothing. Only a bare file name is kept, and only a file in the kit's folder is ever used.
/// </remarks>
public sealed record SeriesKit
{
    public const string FileName = "series.json";
    public const int MinOutroSeconds = 5;
    public const int MaxOutroSeconds = 20; // YouTube end-screen cards can be shown over the last 5 to 20 seconds

    public string ChannelName { get; init; } = "";

    public string Tagline { get; init; } = "";

    public string? Logo { get; init; }

    /// <summary>The few seconds of music the intro plays.</summary>
    public string? Sting { get; init; }

    /// <summary>The music played quietly under every new episode.</summary>
    public string? Music { get; init; }

    public string? Mascot { get; init; }

    public Look Look { get; init; }

    public CaptionStyle Captions { get; init; } = new();

    public int OutroSeconds { get; init; } = 15;

    /// <summary>How the intro is laid out. The outro keeps one layout, as YouTube's end-screen cards need its right half.</summary>
    public IntroLayout Layout { get; init; }

    public TimeSpan Outro => TimeSpan.FromSeconds(OutroSeconds);

    /// <summary>The kit in <paramref name="folder"/>, made safe to use; an empty kit when there is none or it is unreadable.</summary>
    public static SeriesKit Load(string folder)
    {
        var kit = JsonFile.Load(Path.Combine(folder, FileName), new SeriesKit(), ProjectStore.JsonOptions);
        return kit with
        {
            ChannelName = kit.ChannelName ?? "",
            Tagline = kit.Tagline ?? "",
            Logo = Bare(kit.Logo),
            Sting = Bare(kit.Sting),
            Music = Bare(kit.Music),
            Mascot = Bare(kit.Mascot),
            Captions = kit.Captions is { } style && AssCaptions.IsHexColour(style.AccentColor) ? style : new CaptionStyle(),
            OutroSeconds = Math.Clamp(kit.OutroSeconds, MinOutroSeconds, MaxOutroSeconds),
            Layout = Enum.IsDefined(kit.Layout) ? kit.Layout : IntroLayout.Side,
        };

        static string? Bare(string? name) => name is not null && name.Length > 0 && Path.GetFileName(name) == name ? name : null;
    }

    public void Save(string folder) => JsonFile.Save(Path.Combine(folder, FileName), this, ProjectStore.JsonOptions);

    /// <summary>Copies a file the user picked into the kit as its <paramref name="role"/>, and gives the name to keep.</summary>
    public static string Import(string folder, string file, string role)
    {
        var name = role + Path.GetExtension(file).ToLowerInvariant();
        var copy = Path.Combine(folder, name);
        Directory.CreateDirectory(folder);
        if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase))
            File.Copy(file, copy, overwrite: true);
        return name;
    }

    /// <summary>Where one of the kit's files is, or null when it has none or the file has gone.</summary>
    public static string? PathOf(string folder, string? name) =>
        name is not null && Path.GetFileName(name) == name && File.Exists(Path.Combine(folder, name)) ? Path.Combine(folder, name) : null;
}
