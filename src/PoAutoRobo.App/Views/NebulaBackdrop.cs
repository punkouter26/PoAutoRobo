using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

namespace PoAutoRobo.App.Views;

/// <summary>
/// The slow cloud of light in the accent colour behind a page (see <see cref="Nebula"/>), drawn only while there is
/// someone to see it: its page showing and the app in front. Windows' switch for no animation leaves it out altogether.
/// </summary>
public sealed partial class NebulaBackdrop : UserControl
{
    private readonly CanvasAnimatedControl _canvas = new() { ClearColor = Colors.Transparent, IsHitTestVisible = false, IsTabStop = false };
    private PixelShaderEffect<Nebula>? _nebula; // made and used on the drawing thread only
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
            _nebula?.Dispose();
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
        var (accent, size) = (_accent, sender.Size);
        _nebula ??= new PixelShaderEffect<Nebula>();
        _nebula.ConstantBuffer = new Nebula(
            (float)args.Timing.TotalTime.TotalSeconds, new float2((float)size.Width, (float)size.Height),
            new float3(accent.R / 255f, accent.G / 255f, accent.B / 255f), FadeLeft ? 1 : 0);
        // The cloud has no edges of its own, so it is told which part of it to draw: the backdrop's own area.
        args.DrawingSession.DrawImage(_nebula, 0, 0, new Rect(0, 0, size.Width, size.Height));
    }
}
