using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Media.Playback;
using Windows.UI;

namespace PoAutoRobo.App.Views;

/// <summary>
/// A clip's voice made visible while it is auditioned: ribbons of light that swell with each band of the voice's
/// pitch, from its low notes on the left to its high ones on the right, and the words along the bottom, each lighting up and glowing as it is spoken.
/// The voice is read band by band once, before it plays, so the light keeps exact time with what is heard.
/// </summary>
public sealed partial class NarrationScope : UserControl
{
    private const int PerSecond = 30, Bands = 16;

    private readonly CanvasControl _canvas = new() { ClearColor = Colors.Transparent, IsHitTestVisible = false, IsTabStop = false };
    private readonly FrameClock _clock;
    private readonly float[] _shown = new float[Bands]; // eased towards the voice's strengths, so the light flows instead of jumping
    private GaussianBlurEffect? _glow, _ribbonGlow;
    private float[][] _bands = [];
    private Narration? _narration;
    private MediaPlayer? _player;
    private Color _accent;

    public NarrationScope()
    {
        Content = new Grid
        {
            Height = 120,
            CornerRadius = new CornerRadius(8),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(235, 12, 16, 28)),
            Children = { _canvas },
        };
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        _clock = new FrameClock((_, _) => _canvas.Invalidate());
        _canvas.Draw += OnDraw;
        Loaded += (_, _) => _accent = (Color)Application.Current.Resources["SystemAccentColor"];
        Unloaded += (_, _) =>
        {
            _clock.Stop();
            _canvas.RemoveFromVisualTree(); // Win2D controls must be detached explicitly or they leak
        };
    }

    /// <summary>Shows <paramref name="narration"/> as <paramref name="player"/> plays it.</summary>
    public void Show(Narration narration, MediaPlayer player)
    {
        if (!Ui.Effects) return;
        (_narration, _player, _bands) = (narration, player, []);
        Array.Clear(_shown);
        Visibility = Visibility.Visible;
        _clock.Start();
        _ = ReadAsync(narration);
    }

    private async Task ReadAsync(Narration narration)
    {
        try
        {
            var bands = await Task.Run(() => WavInfo.Spectrum(narration.AudioPath, PerSecond, Bands));
            if (ReferenceEquals(narration, _narration)) _bands = bands;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The words still light up in time; only the ribbons stay flat.
        }
    }

    /// <summary>Puts the panel away once the voice has finished.</summary>
    public void Hide()
    {
        _clock.Stop();
        Visibility = Visibility.Collapsed;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_narration is not { } narration || _player is not { } player) return;
        var (w, h) = ((float)sender.Size.Width, (float)sender.Size.Height);
        var position = player.PlaybackSession.Position;

        var bands = _bands;
        var frame = (int)(position.TotalSeconds * PerSecond);
        for (var b = 0; b < Bands; b++)
        {
            var target = frame >= 0 && frame < bands.Length ? bands[frame][b] : 0;
            _shown[b] += (target - _shown[b]) * 0.35f;
        }
        var session = args.DrawingSession;
        DrawVoice(sender, session, w, h * 0.72f);

        // The words around the one being spoken, that one bright, with a glow in the accent colour behind it.
        var words = narration.Words;
        var now = words.Count(word => word.Start <= position) - 1;
        if (now < 0 && words.Count > 0) now = 0;
        if (now < 0) return;
        var (first, last) = (Math.Max(0, now - 3), Math.Min(words.Count - 1, now + 4));
        var line = string.Join(' ', words.Skip(first).Take(last - first + 1).Select(word => word.Text));
        var at = words.Skip(first).Take(now - first).Sum(word => word.Text.Length + 1);
        using var format = new CanvasTextFormat { FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = CanvasHorizontalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
        using var layout = new CanvasTextLayout(sender, line, format, w - 16, 0);
        var y = h - 8 - (float)layout.LayoutBounds.Height;
        layout.SetColor(0, line.Length, Color.FromArgb(140, 255, 255, 255));
        layout.SetColor(at, words[now].Text.Length, Colors.White);
        using var lit = new CanvasCommandList(sender);
        using (var glow = lit.CreateDrawingSession())
        {
            var region = layout.GetCharacterRegions(at, words[now].Text.Length);
            foreach (var r in region)
                glow.FillRoundedRectangle(new Rect(8 + r.LayoutBounds.X - 4, y + r.LayoutBounds.Y - 2, r.LayoutBounds.Width + 8, r.LayoutBounds.Height + 4), 6, 6, _accent);
        }
        _glow ??= new GaussianBlurEffect { BlurAmount = 6 };
        _glow.Source = lit;
        session.DrawImage(_glow, 0, 0, new Rect(0, 0, w, h), 0.55f);
        session.DrawTextLayout(layout, 8, y, Colors.White);
    }
    /// <summary>
    /// Three ribbons of light, each a wave travelling at its own pace, as tall as the voice is strong at that point of its
    /// range, over soft bars mirrored about the middle, one for each band. Low notes in the accent colour, high ones in its
    /// neighbour on the wheel; both ends fade to nothing.
    /// </summary>
    private void DrawVoice(CanvasControl sender, CanvasDrawingSession session, float w, float h)
    {
        var (low, high) = (_accent, Color.FromArgb(255, _accent.G, _accent.B, _accent.R));
        CanvasGradientStop[] stops = [new(0, Faded(low, 0)), new(0.08f, low), new(0.92f, high), new(1, Faded(high, 0))];
        using var colour = new CanvasLinearGradientBrush(sender, stops) { StartPoint = Vector2.Zero, EndPoint = new Vector2(w, 0) };
        var time = _clock.Time;

        for (var b = 0; b < Bands; b++)
        {
            var bar = _shown[b] * 0.42f * h;
            var x = (b + 0.12f) / (Bands - 1) * w;
            session.FillRoundedRectangle(new Rect(x, h / 2 - bar, w / (Bands - 1) * 0.76f, bar * 2), 3, 3, colour);
        }

        using var lines = new CanvasCommandList(sender);
        using (var light = lines.CreateDrawingSession())
            for (var k = 0; k < 3; k++)
            {
                using var path = new CanvasPathBuilder(sender);
                for (var i = 0; i <= 96; i++)
                {
                    var u = i / 96f;
                    var wave = MathF.Sin(u * (7 + k * 4) + time * (1.6f + k * 0.9f)) * MathF.Cos(u * 3 - time * 0.7f);
                    var point = new Vector2(u * w, h * (0.5f + wave * Strength(u) * (0.38f - k * 0.09f)));
                    if (i == 0) path.BeginFigure(point);
                    else path.AddLine(point);
                }
                path.EndFigure(CanvasFigureLoop.Open);
                using var ribbon = CanvasGeometry.CreatePath(path);
                light.DrawGeometry(ribbon, colour, 2.2f - k * 0.5f);
            }
        _ribbonGlow ??= new GaussianBlurEffect { BlurAmount = 5 };
        _ribbonGlow.Source = lines;
        session.DrawImage(_ribbonGlow);
        session.DrawImage(lines);

        static Color Faded(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    // How strong the voice is at a point across the panel, eased between the bands either side so the ribbons bend smoothly.
    private float Strength(float across)
    {
        var place = Math.Clamp(across, 0, 1) * (Bands - 1);
        var band = (int)place;
        var within = place - band;
        within = within * within * (3 - 2 * within);
        return _shown[band] + (_shown[Math.Min(band + 1, Bands - 1)] - _shown[band]) * within;
    }
}
