using System.Numerics;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace PoAutoRobo.App.Views;

/// <summary>
/// The month's spending as liquid in a glass, filled to how much of the budget is gone. What a job would add is a
/// dashed line above it; agreeing to the job pours it in. Its surface is a row of columns pulling on their neighbours,
/// so it ripples where the drops land and sloshes when the level moves. Near the budget it turns amber, and over it
/// red, with bubbles rising as if it were boiling.
/// </summary>
public sealed partial class BudgetGauge : UserControl
{
    private const int Columns = 48;

    private readonly CanvasControl _canvas = new() { ClearColor = Colors.Transparent, IsTabStop = false, Height = 44 };
    private readonly FrameClock _clock;
    private readonly TextBlock _words = new() { Style = (Style)Application.Current.Resources["QuietText"] };
    private readonly float[] _height = new float[Columns], _rise = new float[Columns]; // the surface: each column's height above the level, and how fast it moves
    private readonly List<Vector3> _drops = [];   // falling while it pours: x and y in the glass, and speed
    private readonly List<Vector3> _bubbles = []; // rising while it is over budget
    private readonly Random _random = new();
    private readonly bool _animated = new UISettings().AnimationsEnabled;
    private float _level, _target, _ghost; // shares of the budget, 0 up
    private float _pouring;                // seconds of pouring left
    private Color _accent, _caution, _critical, _glass;
    private TaskCompletionSource? _poured;

    public BudgetGauge()
    {
        Content = new StackPanel { Spacing = 4, Children = { _canvas, _words } };
        _clock = new FrameClock((_, dt) =>
        {
            Step(dt);
            _canvas.Invalidate();
        });
        _canvas.Draw += OnDraw;
        Loaded += (_, _) =>
        {
            TakeColours();
            if (_animated) _clock.Start(); // the liquid moves while the glass is on screen
        };
        ActualThemeChanged += (_, _) => TakeColours();
        // Only stopped, not detached as Win2D asks: a flyout or dialog unloads its content and loads it again, and a
        // detached canvas would be gone when it came back. The glass is small and lives as long as its panel.
        Unloaded += (_, _) =>
        {
            _clock.Stop();
            _poured?.TrySetResult();
        };
    }

    /// <summary>Shows <paramref name="spent"/> of <paramref name="budget"/> gone, and <paramref name="coming"/> more as the line a job would fill to.</summary>
    public void Show(decimal spent, decimal budget, decimal coming = 0)
    {
        var share = budget > 0 ? (float)(spent / budget) : 0;
        _level = _target = share;
        _ghost = budget > 0 ? (float)((spent + coming) / budget) : 0;
        _words.Text = coming > 0
            ? $"{SpendLog.Money(spent)} of this month's {SpendLog.Money(budget)} spent; this adds about {SpendLog.Money(coming)}."
            : $"{SpendLog.Money(spent)} of this month's {SpendLog.Money(budget)} spent.";
        Slosh(0.03f);
        _canvas.Invalidate();
    }

    /// <summary>Pours what is coming into the glass and finishes once it has settled: under a second.</summary>
    public Task PourAsync()
    {
        if (!_animated || _ghost <= _target) return Task.CompletedTask;
        _target = _ghost;
        _pouring = 0.55f;
        Sounds.Play(Cue.Pour, 0.4);
        _poured = new TaskCompletionSource();
        return Task.WhenAny(_poured.Task, Task.Delay(1200)); // never holds anything up for long, whatever the drawing does
    }

    private void TakeColours()
    {
        var resources = Application.Current.Resources;
        _accent = (Color)resources["SystemAccentColor"];
        _caution = ((Microsoft.UI.Xaml.Media.SolidColorBrush)resources["SystemFillColorCautionBrush"]).Color;
        _critical = ((Microsoft.UI.Xaml.Media.SolidColorBrush)resources["SystemFillColorCriticalBrush"]).Color;
        _glass = ((Microsoft.UI.Xaml.Media.SolidColorBrush)resources["TextFillColorSecondaryBrush"]).Color;
    }

    private void Slosh(float strength)
    {
        for (var i = 0; i < Columns; i++)
            _rise[i] += (_random.NextSingle() - 0.5f) * strength;
    }

    private void Step(float dt)
    {
        // The surface: each column is pulled towards its neighbours and back to the level, and slowly comes to rest.
        for (var i = 0; i < Columns; i++)
        {
            var (left, right) = (_height[Math.Max(0, i - 1)], _height[Math.Min(Columns - 1, i + 1)]);
            _rise[i] += ((left + right - 2 * _height[i]) * 900 - _height[i] * 40 - _rise[i] * 3) * dt;
        }
        for (var i = 0; i < Columns; i++)
            _height[i] += _rise[i] * dt;

        // While pouring, drops fall from the spout at the right; each raises the level a little and rings the surface.
        if (_pouring > 0)
        {
            _pouring -= dt;
            for (var n = 0; n < 3; n++)
                _drops.Add(new Vector3(0.82f + (_random.NextSingle() - 0.5f) * 0.04f, -0.1f, 0.5f));
        }
        for (var i = _drops.Count - 1; i >= 0; i--)
        {
            var drop = _drops[i];
            drop.Z += 6 * dt;
            drop.Y += drop.Z * dt;
            if (drop.Y >= 1 - Math.Min(_level, 1.05f))
            {
                var column = Math.Clamp((int)(drop.X * Columns), 0, Columns - 1);
                _rise[column] -= 0.35f;
                _drops.RemoveAt(i);
            }
            else
            {
                _drops[i] = drop;
            }
        }
        _level += (_target - _level) * Math.Min(1, 5 * dt);
        if (_pouring <= 0 && _drops.Count == 0 && Math.Abs(_target - _level) < 0.004f)
            _poured?.TrySetResult();

        // Near the budget the liquid seethes; over it, it boils.
        if (_level > 0.9f && _random.NextSingle() < (_level > 1 ? 0.6f : 0.15f))
            _bubbles.Add(new Vector3(0.05f + _random.NextSingle() * 0.9f, 1, 0.2f + _random.NextSingle() * 0.4f));
        for (var i = _bubbles.Count - 1; i >= 0; i--)
        {
            var bubble = _bubbles[i];
            bubble.Y -= bubble.Z * dt;
            if (bubble.Y < 1 - Math.Min(_level, 1.05f)) _bubbles.RemoveAt(i); else _bubbles[i] = bubble;
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = args.DrawingSession;
        var (w, h) = ((float)sender.Size.Width, (float)sender.Size.Height);
        if (w <= 2) return;
        var inside = new Windows.Foundation.Rect(1, 1, w - 2, h - 2);
        var colour = _ghost > 1 || _level > 1 ? _critical : _ghost > 0.9f || _level > 0.9f ? _caution : _accent;

        // The liquid: up to the level, its top drawn through the surface's columns.
        using (var outline = CanvasGeometry.CreateRoundedRectangle(sender, inside, 8, 8))
        using (session.CreateLayer(1, outline))
        {
            var top = h - 2 - (h - 4) * Math.Min(_level, 1.05f);
            using var path = new CanvasPathBuilder(sender);
            path.BeginFigure(1, h);
            for (var i = 0; i < Columns; i++)
                path.AddLine(1 + (w - 2) * i / (Columns - 1), top + _height[i] * h);
            path.AddLine(w - 1, h);
            path.EndFigure(CanvasFigureLoop.Closed);
            using var liquid = CanvasGeometry.CreatePath(path);
            session.FillGeometry(liquid, Color.FromArgb(200, colour.R, colour.G, colour.B));
            session.DrawGeometry(liquid, Color.FromArgb(255, Mix(colour.R), Mix(colour.G), Mix(colour.B)), 1.5f); // the surface catches the light

            foreach (var bubble in _bubbles)
                session.DrawCircle(bubble.X * w, bubble.Y * h, 2 + bubble.Z * 3, Color.FromArgb(180, 255, 255, 255), 1);
            foreach (var drop in _drops)
                session.FillCircle(drop.X * w, drop.Y * h, 1.6f, colour);

            // What the job would add: a dashed line where the level will be.
            if (_ghost > _target + 0.001f)
            {
                var ghost = h - 2 - (h - 4) * Math.Min(_ghost, 1.05f);
                session.DrawLine(4, ghost, w - 4, ghost, colour, 1.5f, Dashed);
            }
        }
        session.DrawRoundedRectangle(inside, 8, 8, Color.FromArgb(120, _glass.R, _glass.G, _glass.B), 1);
    }

    private static readonly CanvasStrokeStyle Dashed = new() { DashStyle = CanvasDashStyle.Dash };

    private static byte Mix(byte channel) => (byte)(channel + (255 - channel) / 2);
}
