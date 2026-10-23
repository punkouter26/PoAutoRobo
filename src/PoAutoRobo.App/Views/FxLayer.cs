using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;
using static PoAutoRobo.App.Views.Colours;

namespace PoAutoRobo.App.Views;

/// <summary>
/// The window's layer of light: sparks off a card as its clip is done, a puff of smoke when one does not come out,
/// motes drifting while a script is written that gather into each placeholder as its title arrives, and, when the
/// master video is finished, its thumbnail gathering itself out of a swarm of light, then confetti. It lies over every
/// page and never takes a click.
/// </summary>
/// <remarks>
/// Each bit is moved by a little physics worked out here (gravity, the drag of the air, a current that swirls, a
/// spring towards home) and drawn on the graphics card, the glowing ones twice: once sharp and once blurred and added
/// over, which reads as bloom. It is put away while there is nothing to draw, and draws nothing at all when Windows is
/// set to show no animation.
/// </remarks>
public sealed partial class FxLayer : UserControl
{
    private enum Kind { Spark, Smoke, Mote, Seeker, Confetti, Glow }

    private struct Bit
    {
        public Kind Kind;
        public Vector2 At, Speed, Home; // in the layer's own units; Home is where a seeker is going
        public float Age, Life, Size, Angle, Spin, Flip, Depth;
        public Color Colour;
    }

    private const float Gravity = 600;
    private const int Motes = 220; // drifting while a script is written

    private readonly CanvasControl _canvas = new() { ClearColor = Colors.Transparent, IsHitTestVisible = false, IsTabStop = false };
    private readonly List<Bit> _bits = [];
    private readonly Random _random = new();
    private readonly FrameClock _clock;
    private readonly GaussianBlurEffect _bloom = new() { BlurAmount = 7, BorderMode = EffectBorderMode.Soft };
    private readonly GaussianBlurEffect _haze = new() { BlurAmount = 2.5f, BorderMode = EffectBorderMode.Soft };
    private bool _drifting;
    private Rect _driftArea;
    private Color _driftColour;

    public FxLayer()
    {
        Content = _canvas;
        IsHitTestVisible = false;
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Visibility = Visibility.Collapsed; // until there is something to draw
        _clock = new FrameClock(OnFrame);
        _canvas.Draw += OnDraw;
        Unloaded += (_, _) =>
        {
            _clock.Stop();
            _canvas.RemoveFromVisualTree(); // Win2D controls must be detached explicitly or they leak
            _mote?.Dispose();
        };
    }

    // ---- What the window asks for ----

    /// <summary>A burst of sparks from a card whose clip has just been done.</summary>
    public void Sparks(Rect card, Color colour) => Make(random =>
    {
        var middle = Middle(card);
        // The card itself flares for a moment, as the sparks leave it.
        _bits.Add(new Bit
        {
            Kind = Kind.Glow,
            At = middle,
            Life = 0.35f,
            Size = (float)Math.Max(card.Width, card.Height) * 0.6f,
            Colour = colour,
        });
        for (var i = 0; i < 110; i++)
        {
            var angle = random.NextSingle() * MathF.Tau;
            var speed = 160 + random.NextSingle() * 420;
            var from = new Vector2((float)(card.X + random.NextDouble() * card.Width), (float)(card.Y + random.NextDouble() * card.Height));
            var outwards = Vector2.Normalize(from - middle + new Vector2(0.01f, 0)) * 0.6f + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 0.4f;
            _bits.Add(new Bit
            {
                Kind = Kind.Spark,
                At = from,
                Speed = outwards * speed - new Vector2(0, 160),
                Life = 0.7f + random.NextSingle() * 0.6f,
                Size = 1.8f + random.NextSingle() * 2.2f,
                Colour = Lighter(colour, random.NextSingle() * 0.5f),
            });
        }
    });

    /// <summary>A puff of grey smoke that sinks off a card whose clip did not come out.</summary>
    public void Smoke(Rect card) => Make(random =>
    {
        for (var i = 0; i < 26; i++)
            _bits.Add(new Bit
            {
                Kind = Kind.Smoke,
                At = new Vector2((float)(card.X + random.NextDouble() * card.Width), (float)(card.Y + card.Height * (0.3 + random.NextDouble() * 0.5))),
                Speed = new Vector2((random.NextSingle() - 0.5f) * 40, 10 + random.NextSingle() * 30),
                Life = 1 + random.NextSingle() * 0.6f,
                Size = 5 + random.NextSingle() * 5,
                Colour = Color.FromArgb(255, 120, 120, 128),
            });
    });

    /// <summary>Motes drifting on a slow current over <paramref name="area"/>, until <see cref="StopDrifting"/>.</summary>
    public void Drift(Rect area, Color colour)
    {
        (_driftArea, _driftColour, _drifting) = (area, colour, true);
        Make(_ => { });
    }

    /// <summary>The drifting motes fade out where they are.</summary>
    public void StopDrifting() => _drifting = false;

    /// <summary>Light streams in from all round and gathers into <paramref name="target"/>, a placeholder that has just been given its title.</summary>
    public void Gather(Rect target, Color colour) => Make(random =>
    {
        // The drifting motes nearest it are drawn in, and more come from further off.
        var middle = Middle(target);
        for (var i = 0; i < _bits.Count; i++)
            if (_bits[i] is { Kind: Kind.Mote } mote && Vector2.Distance(mote.At, middle) < 260 && random.NextSingle() < 0.6f)
                _bits[i] = mote with { Kind = Kind.Seeker, Home = Within(target, random), Age = 0, Life = 1.6f };
        for (var i = 0; i < 70; i++)
        {
            var angle = random.NextSingle() * MathF.Tau;
            var reach = 120 + random.NextSingle() * 180;
            var at = middle + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * reach;
            _bits.Add(new Bit
            {
                Kind = Kind.Seeker,
                At = at,
                Speed = new Vector2(-MathF.Sin(angle), MathF.Cos(angle)) * 160, // already circling as they come in
                Home = Within(target, random),
                Life = 1.6f,
                Size = 1.4f + random.NextSingle() * 1.4f,
                Colour = Lighter(colour, random.NextSingle() * 0.5f),
            });
        }
    });

    /// <summary>Confetti across the whole window, <paramref name="width"/> wide, near and far, tumbling and fluttering down.</summary>
    public void Confetti(Color accent, float width) => Make(random =>
    {
        Color[] colours = [accent, Turn(accent), Turn(Turn(accent)), Color.FromArgb(255, 255, 214, 64), Colors.White];
        for (var i = 0; i < 280; i++)
        {
            var depth = 0.3f + random.NextSingle() * 0.7f;
            _bits.Add(new Bit
            {
                Kind = Kind.Confetti,
                At = new Vector2(random.NextSingle() * width, -20 - random.NextSingle() * 260),
                Speed = new Vector2((random.NextSingle() - 0.5f) * 160, random.NextSingle() * 120),
                Life = 7,
                Size = 5 + 6 * depth,
                Depth = depth,
                Angle = random.NextSingle() * MathF.Tau,
                Spin = (random.NextSingle() - 0.5f) * 14,
                Flip = random.NextSingle() * MathF.Tau,
                Colour = colours[random.Next(colours.Length)],
            });
        }
    });

    private void Make(Action<Random> make)
    {
        if (!Ui.Effects) return;
        make(_random);
        Visibility = Visibility.Visible;
        _clock.Start();
    }

    // ---- The physics, once a frame ----

    private void OnFrame(float time, float dt)
    {
        if (_bits.Count == 0 && !_drifting && _swarm is null)
        {
            // Nothing left: put away until it is asked for something.
            _clock.Stop();
            Visibility = Visibility.Collapsed;
            return;
        }
        Step(time, dt);
        StepSwarm(time, dt);
        _canvas.Invalidate();
    }

    private void Step(float time, float dt)
    {
        if (_drifting && _bits.Count(b => b.Kind == Kind.Mote) < Motes)
            for (var i = 0; i < 6; i++)
                _bits.Add(new Bit
                {
                    Kind = Kind.Mote,
                    At = Within(_driftArea, _random),
                    Life = 3 + _random.NextSingle() * 3,
                    Size = 1.2f + _random.NextSingle() * 1.6f,
                    Colour = Lighter(_driftColour, _random.NextSingle() * 0.4f),
                });

        for (var i = _bits.Count - 1; i >= 0; i--)
        {
            var bit = _bits[i];
            bit.Age += dt;
            switch (bit.Kind)
            {
                case Kind.Spark:
                    bit.Speed += new Vector2(0, Gravity * dt);
                    bit.Speed *= 1 - 2.2f * dt; // the drag of the air
                    break;
                case Kind.Smoke:
                    bit.Speed += new Vector2(0, 40 * dt);
                    bit.Speed *= 1 - 1.5f * dt;
                    bit.Size += 14 * dt; // spreading as it goes
                    break;
                case Kind.Mote:
                    bit.Speed = Current(bit.At, time) * 45;
                    if (!_drifting) bit.Life = Math.Min(bit.Life, bit.Age + 0.6f); // fade out where it is
                    break;
                case Kind.Seeker:
                    // A spring home, and the current pushing it about less and less as it nears.
                    var pull = bit.Home - bit.At;
                    bit.Speed += (pull * 55 - bit.Speed * 9 + Current(bit.At, time) * 220 * MathF.Max(0, 1 - bit.Age * 2)) * dt;
                    if (pull.LengthSquared() < 9 && bit.Age > 0.35f) bit.Life = bit.Age; // home: it goes out
                    break;
                case Kind.Confetti:
                    // Falls under gravity, held back hard by the air, and pushed side to side as it flutters, which it does as it turns.
                    bit.Speed += new Vector2(MathF.Sin(bit.Flip * 2) * 260, Gravity * 0.55f) * dt;
                    bit.Speed *= 1 - 1.8f * dt;
                    bit.Angle += bit.Spin * dt;
                    bit.Flip += (4 + bit.Depth * 3) * dt;
                    if (bit.At.Y > ActualHeight + 40) bit.Life = bit.Age;
                    break;
            }
            bit.At += bit.Speed * dt;
            if (bit.Age >= bit.Life) _bits.RemoveAt(i); else _bits[i] = bit;
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        DrawSwarm(sender, args.DrawingSession);
        if (_bits.Count == 0) return;
        using var glowing = new CanvasCommandList(sender);
        using var far = new CanvasCommandList(sender);
        using var near = new CanvasCommandList(sender);
        using (var light = glowing.CreateDrawingSession())
        using (var back = far.CreateDrawingSession())
        using (var front = near.CreateDrawingSession())
        {
            foreach (var bit in _bits)
            {
                // Each comes up quickly and fades over the last part of its life.
                var shown = Math.Clamp(bit.Age / 0.12f, 0, 1) * Math.Clamp((bit.Life - bit.Age) / (bit.Life * 0.45f), 0, 1);
                switch (bit.Kind)
                {
                    case Kind.Spark:
                        // A short streak along its path: a spark is seen by where it has just been.
                        light.DrawLine(bit.At - bit.Speed * 0.04f, bit.At, Faded(bit.Colour, shown), bit.Size, Round);
                        break;
                    case Kind.Glow:
                        light.FillCircle(bit.At, bit.Size * (0.6f + bit.Age / bit.Life), Faded(bit.Colour, 0.45f * (1 - bit.Age / bit.Life)));
                        break;
                    case Kind.Mote or Kind.Seeker:
                        light.FillCircle(bit.At, bit.Size, Faded(bit.Colour, shown * (bit.Kind == Kind.Mote ? 0.6f : 1)));
                        break;
                    case Kind.Smoke:
                        back.FillCircle(bit.At, bit.Size, Faded(bit.Colour, shown * 0.3f));
                        break;
                    case Kind.Confetti:
                        // A flat piece turning in the air: its width narrows and widens as it flips, and it darkens edge-on.
                        var width = MathF.Abs(MathF.Cos(bit.Flip));
                        var session = bit.Depth < 0.6f ? back : front;
                        session.Transform = Matrix3x2.CreateScale(0.4f + 0.6f * width, 1) * Matrix3x2.CreateRotation(bit.Angle) * Matrix3x2.CreateTranslation(bit.At);
                        session.FillRectangle(-bit.Size / 2, -bit.Size * 0.8f, bit.Size, bit.Size * 1.6f, Faded(Shade(bit.Colour, 0.55f + 0.45f * width), shown * (0.5f + 0.5f * bit.Depth)));
                        session.Transform = Matrix3x2.Identity;
                        break;
                }
            }
        }

        var draw = args.DrawingSession;
        _haze.Source = far; // the far pieces out of focus, behind
        draw.DrawImage(_haze);
        draw.DrawImage(near);
        _bloom.Source = glowing;
        draw.Blend = CanvasBlend.Add; // light adds to what is behind it
        draw.DrawImage(_bloom);
        draw.DrawImage(glowing);
        draw.Blend = CanvasBlend.SourceOver;
    }

    // ---- Small helpers ----

    private static readonly Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle Round = new()
    {
        StartCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
        EndCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
    };

    // A current that turns and folds but never gathers or drains: the curl of a field of crossing waves.
    private static Vector2 Current(Vector2 at, float t)
    {
        var (x, y) = (at.X / 180, at.Y / 180);
        var (a, b) = (3.1f * x + 0.5f * t, 2.3f * y - 0.4f * t);
        var dy = -2.3f * MathF.Sin(a) * MathF.Sin(b) + 1.4f * MathF.Cos(1.7f * y + 0.3f * t);
        var dx = 3.1f * MathF.Cos(a) * MathF.Cos(b) - 1.1f * MathF.Sin(1.3f * x - 0.2f * t);
        return new Vector2(dy, -dx) * 0.3f;
    }

    private static Vector2 Middle(Rect r) => new((float)(r.X + r.Width / 2), (float)(r.Y + r.Height / 2));

    private static Vector2 Within(Rect r, Random random) => new((float)(r.X + random.NextDouble() * r.Width), (float)(r.Y + random.NextDouble() * r.Height));

    private static Color Lighter(Color c, float by) =>
        Color.FromArgb(255, (byte)(c.R + (255 - c.R) * by), (byte)(c.G + (255 - c.G) * by), (byte)(c.B + (255 - c.B) * by));

    private static Color Shade(Color c, float by) => Color.FromArgb(255, (byte)(c.R * by), (byte)(c.G * by), (byte)(c.B * by));

    // The colour a third of the way round the wheel.
    private static Color Turn(Color c) => Color.FromArgb(255, c.B, c.R, c.G);
}
