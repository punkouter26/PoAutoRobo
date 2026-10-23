using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
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
/// pitch (see <see cref="Aurora"/>), and the words along the bottom, each lighting up and glowing as it is spoken.
/// The voice is read band by band once, before it plays, so the light keeps exact time with what is heard.
/// </summary>
public sealed partial class NarrationScope : UserControl
{
    private const int PerSecond = 30, Bands = 16;

    private readonly CanvasControl _canvas = new() { ClearColor = Colors.Transparent, IsHitTestVisible = false, IsTabStop = false };
    private readonly FrameClock _clock;
    private readonly float[] _shown = new float[Bands]; // eased towards the voice's strengths, so the light flows instead of jumping
    private PixelShaderEffect<Aurora>? _aurora;
    private GaussianBlurEffect? _glow;
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
            _aurora?.Dispose();
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
        _aurora ??= new PixelShaderEffect<Aurora>();
        _aurora.ConstantBuffer = new Aurora(_clock.Time, new float2(w, h * 0.72f), new float3(_accent.R / 255f, _accent.G / 255f, _accent.B / 255f),
            new float4(_shown[0], _shown[1], _shown[2], _shown[3]), new float4(_shown[4], _shown[5], _shown[6], _shown[7]),
            new float4(_shown[8], _shown[9], _shown[10], _shown[11]), new float4(_shown[12], _shown[13], _shown[14], _shown[15]));
        var session = args.DrawingSession;
        session.DrawImage(_aurora, 0, 0, new Rect(0, 0, w, h * 0.72f));

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
}
