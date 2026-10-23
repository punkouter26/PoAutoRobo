namespace PoAutoRobo.Core.Render;

/// <summary>
/// The series' own pictures when it has none: a robot's head for the logo, and a robot standing ready for its lesson
/// for the mascot, in white and grey with the series colour in its eyes and lights, worked out pixel by pixel. Like
/// <see cref="Synth"/>, nothing is shipped or paid for, and the same colour always gives the same picture.
/// </summary>
public static class Emblem
{
    public const int LogoSize = 512, MascotWidth = 512, MascotHeight = 768;

    private static readonly (byte R, byte G, byte B) Shell = (240, 243, 247), Joint = (150, 158, 170), Visor = (27, 34, 48), Ink = (20, 24, 34);

    /// <summary>Distance from a point to a shape's edge in pixels: negative inside, positive outside.</summary>
    private delegate float Shape(float x, float y);

    /// <summary>The logo: a robot's head, its visor and glowing eyes, an antenna and two ear bolts.</summary>
    /// <returns>Plain RGBA pixels, row by row, <see cref="LogoSize"/> square, see-through around the head.</returns>
    public static byte[] Logo((byte R, byte G, byte B) accent)
    {
        var pixels = new byte[LogoSize * LogoSize * 4];
        void Part(Shape shape, (byte, byte, byte) colour, bool outlined = true) => Paint(pixels, LogoSize, LogoSize, shape, colour, outlined ? 12 : 0);
        Part(Capsule(256, 50, 256, 110, 9), Joint);
        Part(Circle(256, 48, 24), accent);
        Part(Box(68, 280, 26, 56, 14), Joint);
        Part(Box(444, 280, 26, 56, 14), Joint);
        Part(Box(256, 285, 172, 160, 64), Shell);
        Part(Box(256, 275, 128, 74, 60), Visor, outlined: false);
        Part(Circle(206, 275, 26), accent, outlined: false);
        Part(Circle(306, 275, 26), accent, outlined: false);
        Part(Box(256, 395, 44, 8, 6), accent, outlined: false);
        return pixels;
    }

    /// <summary>The mascot: a robot standing ready, arms at its sides, a light on its chest.</summary>
    /// <returns>Plain RGBA pixels, row by row, <see cref="MascotWidth"/> by <see cref="MascotHeight"/>, see-through around the robot.</returns>
    public static byte[] Mascot((byte R, byte G, byte B) accent)
    {
        var pixels = new byte[MascotWidth * MascotHeight * 4];
        void Part(Shape shape, (byte, byte, byte) colour, bool outlined = true) => Paint(pixels, MascotWidth, MascotHeight, shape, colour, outlined ? 7 : 0);
        // Back to front, so each part's outline is drawn over what lies behind it.
        Part(Capsule(212, 480, 206, 680, 32), Shell);
        Part(Capsule(300, 480, 306, 680, 32), Shell);
        Part(Box(196, 712, 56, 22, 14), Visor);
        Part(Box(316, 712, 56, 22, 14), Visor);
        Part(Capsule(150, 280, 124, 452, 26), Shell);
        Part(Capsule(362, 280, 388, 452, 26), Shell);
        Part(Circle(122, 470, 30), Joint);
        Part(Circle(390, 470, 30), Joint);
        Part(Box(256, 465, 72, 26, 12), Joint);
        Part(Box(256, 345, 98, 98, 32), Shell);
        Part(Box(256, 332, 46, 32, 10), Visor, outlined: false);
        Part(Circle(256, 332, 15), accent, outlined: false);
        Part(Box(256, 236, 26, 20, 6), Joint);
        Part(Capsule(256, 38, 256, 64, 6), Joint);
        Part(Circle(256, 32, 13), accent);
        Part(Box(256, 140, 98, 86, 40), Shell);
        Part(Box(256, 138, 72, 42, 36), Visor, outlined: false);
        Part(Circle(226, 138, 14), accent, outlined: false);
        Part(Circle(286, 138, 14), accent, outlined: false);
        return pixels;
    }

    private static Shape Circle(float cx, float cy, float r) => (x, y) => MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;

    /// <summary>A box <paramref name="hx"/> by <paramref name="hy"/> either side of its centre, its corners rounded by <paramref name="r"/>.</summary>
    private static Shape Box(float cx, float cy, float hx, float hy, float r) => (x, y) =>
    {
        var (qx, qy) = (MathF.Abs(x - cx) - hx + r, MathF.Abs(y - cy) - hy + r);
        return MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0)) + MathF.Min(MathF.Max(qx, qy), 0) - r;
    };

    /// <summary>A rod with rounded ends from one point to another, <paramref name="r"/> thick either side.</summary>
    private static Shape Capsule(float ax, float ay, float bx, float by, float r) => (x, y) =>
    {
        var (px, py, dx, dy) = (x - ax, y - ay, bx - ax, by - ay);
        var along = Math.Clamp((px * dx + py * dy) / (dx * dx + dy * dy), 0, 1);
        var (ex, ey) = (px - dx * along, py - dy * along);
        return MathF.Sqrt(ex * ex + ey * ey) - r;
    };

    /// <summary>
    /// Lays a shape over what is drawn so far, its edge smoothed over one pixel, with a dark outline
    /// <paramref name="outline"/> pixels wide round it first when asked.
    /// </summary>
    private static void Paint(byte[] pixels, int width, int height, Shape shape, (byte R, byte G, byte B) colour, float outline)
    {
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var edge = shape(x + 0.5f, y + 0.5f);
                if (edge > outline + 1) continue;
                if (outline > 0) Blend(pixels, (y * width + x) * 4, Ink, Math.Clamp(0.5f - (edge - outline), 0, 1));
                Blend(pixels, (y * width + x) * 4, colour, Math.Clamp(0.5f - edge, 0, 1));
            }
    }

    // One colour over another, each with its own see-through-ness, as paint over paint.
    private static void Blend(byte[] pixels, int at, (byte R, byte G, byte B) colour, float cover)
    {
        if (cover <= 0) return;
        var under = pixels[at + 3] / 255f;
        var alpha = cover + under * (1 - cover);
        float Mix(byte top, byte bottom) => (top * cover + bottom * under * (1 - cover)) / alpha;
        (pixels[at], pixels[at + 1], pixels[at + 2]) = ((byte)Mix(colour.R, pixels[at]), (byte)Mix(colour.G, pixels[at + 1]), (byte)Mix(colour.B, pixels[at + 2]));
        pixels[at + 3] = (byte)(alpha * 255);
    }
}
