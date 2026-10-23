using System.Numerics;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace PoAutoRobo.App.Views;

/// <summary>
/// The slow cloud of light in the accent colour behind a page: Win2D's own fractal noise, drifting, turned into soft
/// light in the accent colour. Drawn only while there is
/// someone to see it: its page showing and the app in front. Windows' switch for no animation leaves it out altogether.
/// </summary>
public sealed partial class NebulaBackdrop : UserControl
{
    private readonly CanvasAnimatedControl _canvas = new() { ClearColor = Colors.Transparent, IsHitTestVisible = false, IsTabStop = false };
    private TurbulenceEffect? _noise;          // these two are made and used on the drawing thread only
    private ColorMatrixEffect? _light;
    private Color _accent;                     // read by the drawing thread, set by the page
    private bool _wanted, _inFront = true, _loaded;

    public NebulaBackdrop()
    {
        Content = _canvas;
        IsHitTestVisible = false;
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        _canvas.TargetElapsedTime = TimeSpan.FromMilliseconds(33); // thirty frames a second is plenty for something this slow
        _canvas.Draw += OnDraw;
        Loaded += (_, _) =>
        {
            _loaded = true;
            _accent = (Color)Application.Current.Resources["SystemAccentColor"];
            Pace();
        };
        ActualThemeChanged += (_, _) => _accent = (Color)Application.Current.Resources["SystemAccentColor"];
        Unloaded += (_, _) =>
        {
            _loaded = false;
            _canvas.RemoveFromVisualTree(); // Win2D controls must be detached explicitly or they leak
            _light?.Dispose();
            _noise?.Dispose();
        };
    }

    /// <summary>Fades the cloud out towards the left edge, where a page's form begins.</summary>
    public bool FadeLeft { get; set; }

    /// <summary>True while the page it is behind is the one showing. Set again to look afresh at the Effects switch.</summary>
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

    // Drawn only with Effects on in Settings, and asked again each time anything here changes, so the switch acts at once.
    private void Pace()
    {
        Visibility = Ui.Effects ? Visibility.Visible : Visibility.Collapsed;
        if (_loaded) _canvas.Paused = !(_wanted && _inFront && Ui.Effects);
    }

    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var (accent, size, time) = (_accent, sender.Size.ToVector2(), (float)args.Timing.TotalTime.TotalSeconds);
        _noise ??= new TurbulenceEffect { Octaves = 5, Noise = TurbulenceEffectNoise.FractalSum };
        _noise.Size = size;
        _noise.Frequency = new Vector2(1.1f / Math.Max(1, size.Y)); // the same in both directions, so the folds are not stretched by the window's shape
        _noise.Offset = new Vector2(time * 4, time * 2.5f);             // a slow drift
        // The noise's first channel becomes light: none in its lower half, up to a quarter strength at its peaks, all in
        // the accent colour. The effect takes the noise's own see-through-ness off first and puts the light's back after.
        const float Rise = 0.8f, Floor = -0.36f;
        _light ??= new ColorMatrixEffect();
        _light.Source = _noise;
        _light.ColorMatrix = new Matrix5x4
        {
            M14 = Rise,
            M54 = Floor,
            M51 = accent.R / 255f,
            M52 = accent.G / 255f,
            M53 = accent.B / 255f,
        };
        var session = args.DrawingSession;
        if (!FadeLeft)
        {
            session.DrawImage(_light);
            return;
        }
        // Faded out towards the left edge, where a page's form begins.
        using var fade = new CanvasLinearGradientBrush(session, Colors.Transparent, Colors.White) { StartPoint = Vector2.Zero, EndPoint = new Vector2(size.X * 0.25f, 0) };
        using (session.CreateLayer(fade))
            session.DrawImage(_light);
    }
}
