using System.Collections.Specialized;
using System.Numerics;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace PoAutoRobo.App.Views;

/// <summary>
/// A thread of light through the deck's cards in running order, lying behind them so their frosted glass blurs it.
/// It hangs between each card and the next, sags under its own weight and swings when the cards move, is pulled taut
/// for a moment when the order changes, and glows red at a card whose clip runs too short or too long. A pulse of light
/// travels along it from the first clip to the last.
/// </summary>
/// <remarks>
/// A rope of beads joined by links that keep their length, moved by Verlet steps: each bead goes on as it was going,
/// gravity pulls it down, and the links are then made right a few times over. The ends of each span are held at the
/// middles of the two cards, read from the deck each frame, so it follows scrolling, reordering and resizing.
/// </remarks>
public sealed partial class RunningThread : UserControl
{
    private const int Beads = 12;          // between one card and the next
    private const float Slack = 1.07f;     // a span's length, against the straight line between its cards
    private const float Gravity = 900;

    private readonly CanvasControl _canvas = new() { IsHitTestVisible = false, IsTabStop = false };
    private readonly FrameClock _clock;
    private readonly bool _animated = new UISettings().AnimationsEnabled;
    private readonly List<Span> _spans = [];
    private GridView? _deck;
    private IList<ClipViewModel> _cards = [];
    private bool _wanted, _inFront = true, _loaded;
    private float _taut; // 1 just after the order changes, easing back to 0

    private sealed class Span
    {
        public Guid From, To;
        public Vector2[] At = new Vector2[Beads + 2], Was = new Vector2[Beads + 2];
        public bool Placed;
    }

    public RunningThread()
    {
        Content = _canvas;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        _clock = new FrameClock(OnFrame);
        _canvas.Draw += OnDraw;
        Loaded += (_, _) =>
        {
            _loaded = true;
            if (!_animated) Visibility = Visibility.Collapsed;
            Pace();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            _clock.Stop();
            _canvas.RemoveFromVisualTree(); // Win2D controls must be detached explicitly or they leak
        };
    }

    /// <summary>The deck it runs through, and the cards in it, in running order.</summary>
    public void Follow(GridView deck, System.Collections.ObjectModel.ObservableCollection<ClipViewModel> cards)
    {
        (_deck, _cards) = (deck, cards);
        // While it is paused (another app in front), a change to the deck's layout still moves the thread to match,
        // so it is never left hanging where the cards were.
        deck.LayoutUpdated += (_, _) =>
        {
            if (_loaded && _animated && _wanted && !_clock.Running) OnFrame(_clock.Time, 0);
        };
        cards.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Move or NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove) _taut = 1;
        };
    }

    /// <summary>True while the deck is showing.</summary>
    public bool Wanted
    {
        get => _wanted;
        set
        {
            _wanted = value;
            Pace();
        }
    }

    /// <summary>Told by the window: false while another app is in front, when there is nobody to draw for.</summary>
    public void SetInFront(bool inFront)
    {
        _inFront = inFront;
        Pace();
    }

    private void Pace()
    {
        if (_loaded && _animated && _wanted && _inFront) _clock.Start(); else _clock.Stop();
    }

    // ---- The physics, once a frame ----

    private void OnFrame(float time, float dt)
    {
        if (_deck is null) return;
        var middles = Middles();
        Rebuild(middles);
        _taut = MathF.Max(0, _taut - dt * 1.6f);
        var slack = Slack - 0.09f * _taut;
        foreach (var span in _spans)
        {
            if (!middles.TryGetValue(span.From, out var a) || !middles.TryGetValue(span.To, out var b)) continue;
            if (!span.Placed)
            {
                // A new span starts hanging straight, and drops into its curve.
                for (var i = 0; i < span.At.Length; i++)
                    span.At[i] = span.Was[i] = Vector2.Lerp(a, b, i / (float)(Beads + 1));
                span.Placed = true;
            }
            // Each bead carries on as it was going, held back a little by the air, and falls.
            for (var i = 1; i <= Beads; i++)
            {
                var now = span.At[i];
                span.At[i] += (now - span.Was[i]) * 0.985f + new Vector2(0, Gravity * dt * dt);
                span.Was[i] = now;
            }
            // The links are put back to their length, the ends held on the cards.
            var link = Vector2.Distance(a, b) * slack / (Beads + 1);
            for (var round = 0; round < 10; round++)
            {
                span.At[0] = a;
                span.At[^1] = b;
                for (var i = 0; i < span.At.Length - 1; i++)
                {
                    var gap = span.At[i + 1] - span.At[i];
                    var length = gap.Length();
                    if (length < 0.001f) continue;
                    // The ends are held, so a link at an end is put right by its free bead alone.
                    var fix = gap * ((length - link) / length);
                    var (first, last) = (i == 0, i + 1 == span.At.Length - 1);
                    if (first && last) continue;
                    if (first) span.At[i + 1] -= fix;
                    else if (last) span.At[i] += fix;
                    else
                    {
                        span.At[i] += fix * 0.5f;
                        span.At[i + 1] -= fix * 0.5f;
                    }
                }
            }
        }
        _canvas.Invalidate();
    }

    // The middle of every card on screen, in this control's units.
    private Dictionary<Guid, Vector2> Middles()
    {
        var middles = new Dictionary<Guid, Vector2>();
        if (_deck is null || XamlRoot is null) return middles;
        foreach (var card in _cards)
        {
            if (_deck.ContainerFromItem(card) is not FrameworkElement box || box.ActualWidth <= 0) continue;
            var at = box.TransformToVisual(this).TransformPoint(new Point(box.ActualWidth / 2, box.ActualHeight * 0.4));
            middles[card.Id] = new Vector2((float)at.X, (float)at.Y);
        }
        return middles;
    }

    // One span between each card and the next; a span already hanging is kept, so a reorder swings rather than jumps.
    private void Rebuild(Dictionary<Guid, Vector2> middles)
    {
        var wanted = new List<(Guid, Guid)>();
        for (var i = 0; i + 1 < _cards.Count; i++)
            wanted.Add((_cards[i].Id, _cards[i + 1].Id));
        if (wanted.Count == _spans.Count && wanted.Select((w, i) => _spans[i].From == w.Item1 && _spans[i].To == w.Item2).All(same => same)) return;
        var old = _spans.ToList();
        _spans.Clear();
        foreach (var (from, to) in wanted)
        {
            // A span that ran from the same card carries over its beads, which then swing to the new card.
            var kept = old.FirstOrDefault(s => s.From == from) ?? old.FirstOrDefault(s => s.To == to);
            var span = new Span { From = from, To = to };
            if (kept is { Placed: true })
            {
                kept.At.CopyTo(span.At, 0);
                kept.Was.CopyTo(span.Was, 0);
                span.Placed = true;
            }
            _spans.Add(span);
        }
    }

    // ---- Drawing ----

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!_animated || !_wanted || _spans.Count == 0) return;
        var accent = (Color)Application.Current.Resources["SystemAccentColor"];
        var warn = Color.FromArgb(255, 232, 72, 85);
        var light = ActualTheme == ElementTheme.Light;
        var session = args.DrawingSession;
        var time = _clock.Time;
        var warnings = _cards.Where(c => c.LengthWarning.Length > 0).Select(c => c.Id).ToHashSet();

        for (var s = 0; s < _spans.Count; s++)
        {
            var span = _spans[s];
            if (!span.Placed) continue;
            using var path = new CanvasPathBuilder(sender);
            path.BeginFigure(span.At[0]);
            // Through the middle of each pair of beads, curving at the beads themselves, so the thread runs smooth.
            for (var i = 1; i < span.At.Length - 1; i++)
                path.AddQuadraticBezier(span.At[i], (span.At[i] + span.At[i + 1]) / 2);
            path.AddLine(span.At[^1]);
            path.EndFigure(CanvasFigureLoop.Open);
            using var line = CanvasGeometry.CreatePath(path);

            var colour = warnings.Contains(span.From) || warnings.Contains(span.To) ? Mix(accent, warn, 0.5f) : accent;
            var glow = light ? 0.34f : 0.3f;
            session.DrawGeometry(line, Faded(colour, glow * 0.5f), 9, Round);
            session.DrawGeometry(line, Faded(colour, glow), 4, Round);
            session.DrawGeometry(line, Faded(light ? colour : Mix(colour, Colors.White, 0.6f), 0.85f), 1.4f, Round);

            // The pulse: a bead of light running from the first clip towards the last, one span after another.
            var along = (time * 0.6f - s * 0.35f) % 1f;
            if (along < 0) along += 1;
            var bead = Along(span.At, along);
            session.FillCircle(bead, 9, Faded(colour, 0.18f));
            session.FillCircle(bead, 3.2f, Faded(Mix(colour, Colors.White, 0.7f), 0.95f));
        }

        // A knot at each card, red at one whose length is wrong, swelling slowly as if breathing.
        foreach (var span in _spans.Where(s => s.Placed))
            foreach (var (end, id) in new[] { (span.At[0], span.From), (span.At[^1], span.To) })
            {
                var wrong = warnings.Contains(id);
                var colour = wrong ? warn : accent;
                var breathe = wrong ? 1 + 0.35f * MathF.Sin(time * 4) : 1;
                session.FillCircle(end, 8 * breathe, Faded(colour, wrong ? 0.45f : 0.2f));
                session.FillCircle(end, 3.5f, Faded(Mix(colour, Colors.White, 0.5f), 0.9f));
            }
    }

    private static Vector2 Along(Vector2[] beads, float fraction)
    {
        var place = fraction * (beads.Length - 1);
        var i = Math.Min((int)place, beads.Length - 2);
        return Vector2.Lerp(beads[i], beads[i + 1], place - i);
    }

    private static readonly CanvasStrokeStyle Round = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };

    private static Color Faded(Color c, float alpha) => Color.FromArgb((byte)(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, float t) =>
        Color.FromArgb(255, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
