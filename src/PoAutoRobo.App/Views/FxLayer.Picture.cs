using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace PoAutoRobo.App.Views;

// The finished video's thumbnail gathering itself out of a swarm of light: one mote for every point of the picture,
// carried in on a swirling current from all round the window, each sprung home to its own place and colour.
public sealed partial class FxLayer
{
    private const int Across = 192, Down = 108; // motes per row and per column: some twenty thousand in all
    private const float Arrive = 2.6f, Hold = 1.3f, Leave = 1.1f; // seconds to gather, to show the picture, to blow away

    private sealed class Gathering
    {
        public required Vector2[] At, Speed, Home;
        public required Color[] Colour;
        public required float[] Start; // when each mote begins to be drawn home, so the picture fills from its middle out
        public required Rect Area;
        public float Age;
    }

    private Gathering? _swarm;
    private Action? _landed; // told once, as the picture comes together
    private CanvasBitmap? _mote;

    /// <summary>
    /// The picture at <paramref name="path"/> gathers itself in <paramref name="area"/> out of motes of light that swirl
    /// in from all round, holds a moment, then blows away. Calls <paramref name="landed"/> as it comes together.
    /// </summary>
    public async void Assemble(string path, Rect area, Action landed)
    {
        if (!Ui.Effects || area.Width < 8)
        {
            landed();
            return;
        }
        Color[] pixels;
        try
        {
            using var picture = await CanvasBitmap.LoadAsync(_canvas, path);
            using var small = new CanvasRenderTarget(_canvas, Across, Down, 96);
            using (var session = small.CreateDrawingSession())
                session.DrawImage(picture, new Rect(0, 0, Across, Down), picture.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
            pixels = small.GetPixelColors();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException || e.HResult == unchecked((int)0x88982F50))
        {
            landed(); // no picture to gather: the celebration goes on without it
            return;
        }

        var count = Across * Down;
        var middle = Middle(area);
        var reach = (float)Math.Max(ActualWidth, ActualHeight) * 0.75f;
        var swarm = new Gathering { At = new Vector2[count], Speed = new Vector2[count], Home = new Vector2[count], Colour = pixels, Start = new float[count], Area = area };
        var cell = new Vector2((float)area.Width / Across, (float)area.Height / Down);
        for (var i = 0; i < count; i++)
        {
            var (x, y) = (i % Across, i / Across);
            swarm.Home[i] = new Vector2((float)area.X, (float)area.Y) + new Vector2(x + 0.5f, y + 0.5f) * cell;
            // From a wide ring round the window, already circling as they come.
            var angle = _random.NextSingle() * MathF.Tau;
            var from = reach * (0.7f + 0.5f * _random.NextSingle());
            swarm.At[i] = middle + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * from;
            swarm.Speed[i] = new Vector2(-MathF.Sin(angle), MathF.Cos(angle)) * (380 + 260 * _random.NextSingle());
            var outward = Vector2.Distance(swarm.Home[i], middle) / ((float)area.Width * 0.6f);
            swarm.Start[i] = 0.5f + 1.1f * Math.Clamp(outward, 0, 1) + 0.25f * _random.NextSingle();
        }
        (_swarm, _landed) = (swarm, landed);
        Make(_ => { });
    }

    private void StepSwarm(float time, float dt)
    {
        if (_swarm is not { } swarm) return;
        swarm.Age += dt;
        var age = swarm.Age;
        if (age > Arrive && _landed is { } landed)
        {
            _landed = null;
            landed();
        }
        if (age > Arrive + Hold + Leave)
        {
            _swarm = null;
            return;
        }
        var leaving = age > Arrive + Hold;
        for (var i = 0; i < swarm.At.Length; i++)
        {
            var at = swarm.At[i];
            var speed = swarm.Speed[i];
            if (leaving)
            {
                // Blown off on the current, rising as warm air does.
                speed += (Current(at, time) * 900 + new Vector2(0, -260)) * dt;
            }
            else
            {
                // Swirled about by the current until its turn comes, then sprung home ever more firmly, and held there still.
                var drawn = Math.Clamp((age - swarm.Start[i]) / 0.9f, 0, 1);
                drawn = drawn * drawn * (3 - 2 * drawn);
                var pull = swarm.Home[i] - at;
                speed += (Current(at, time) * 1400 * (1 - drawn) + pull * (4 + 160 * drawn) - speed * (0.6f + 14 * drawn)) * dt;
                if (drawn >= 1 && pull.LengthSquared() < 0.25f) speed = Vector2.Zero;
            }
            swarm.Speed[i] = speed;
            swarm.At[i] = at + speed * dt;
        }
    }

    private void DrawSwarm(CanvasControl sender, CanvasDrawingSession draw)
    {
        if (_swarm is not { } swarm) return;
        var age = swarm.Age;
        var shown = Math.Clamp(age / 0.3f, 0, 1) * Math.Clamp((Arrive + Hold + Leave - age) / Leave, 0, 1);
        var cell = (float)swarm.Area.Width / Across;
        // A sweep of light crosses the picture once it has come together, as it would a print just made.
        var sweep = (age - Arrive) / Hold;

        _mote ??= Mote(sender);
        var scale = cell * 1.5f / 8;
        var useBatch = CanvasSpriteBatch.IsSupported(sender.Device);
        using var batch = useBatch ? draw.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear, CanvasSpriteOptions.None) : null;
        for (var i = 0; i < swarm.At.Length; i++)
        {
            var c = swarm.Colour[i];
            var shine = sweep is > 0 and < 1 ? MathF.Max(0, 1 - MathF.Abs(swarm.Home[i].X - (float)(swarm.Area.X + swarm.Area.Width * sweep)) / 40) * 0.6f : 0;
            var tint = new Vector4(c.R / 255f + shine, c.G / 255f + shine, c.B / 255f + shine, shown);
            if (batch is not null)
                batch.Draw(_mote, Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(swarm.At[i] - new Vector2(4 * scale)), tint);
            else
                draw.FillRectangle(swarm.At[i].X - cell / 2, swarm.At[i].Y - cell / 2, cell * 1.2f, cell * 1.2f, Color.FromArgb((byte)(255 * shown), c.R, c.G, c.B));
        }
    }

    // A soft round dot of white, tinted per mote as it is drawn.
    private static CanvasBitmap Mote(ICanvasResourceCreator device)
    {
        var dots = new Color[64];
        for (var i = 0; i < 64; i++)
        {
            var (x, y) = (i % 8 - 3.5f, i / 8 - 3.5f);
            var alpha = Math.Clamp(1.25f - MathF.Sqrt(x * x + y * y) / 3.6f, 0, 1);
            var a = (byte)(alpha * 255);
            dots[i] = Color.FromArgb(a, a, a, a); // light and colour multiplied together, as the drawing surface expects
        }
        return CanvasBitmap.CreateFromColors(device, dots, 8, 8);
    }
}
