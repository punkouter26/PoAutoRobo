using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PoAutoRobo.App.ViewModels;
using Windows.Foundation;

namespace PoAutoRobo.App.Views;

public sealed partial class ClipCard : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(ClipViewModel), typeof(ClipCard),
        new PropertyMetadata(null, (d, _) => ((ClipCard)d).Bindings.Update())); // recycled containers get a new clip

    private CanvasRenderTarget? _picture; // the card's picture at the size it is shown, for the lens to look through
    private string? _pictureOf;           // the file it was made from
    private Point _pointer;
    private bool _over; // the pointer is on the card, so the lens is drawn

    public ClipCard()
    {
        InitializeComponent();
        ElementCompositionPreview.GetElementVisual(LensView).Opacity = 0; // faded in and out by the same visual; see Fade
        // ponytail: the lens's canvas is not detached as Win2D asks, because the deck recycles cards and loads them
        // again; only its pictures are let go of. A card that is truly gone may keep a little memory until the window
        // closes. Detach it in the deck's own cleanup if long sessions ever show the growth.
        Unloaded += (_, _) =>
        {
            _picture?.Dispose();
            (_picture, _pictureOf) = (null, null);
        };
    }

    /// <summary>Thumbnail for a picture path; null (nothing drawn) when the clip has no picture.</summary>
    public static ImageSource? ToImage(string? path) =>
        string.IsNullOrEmpty(path) ? null : new BitmapImage(new Uri(path)) { DecodePixelWidth = 400 };

    public ClipViewModel? Card
    {
        get => (ClipViewModel?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    // The card lifts and offers its play button under the pointer, and puts both away after.
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SpringTo(1.03f);
        PlayButton.Opacity = 1;
        if (!Ui.Effects) return;
        _over = true;
        Perspective();
        _ = ShowLensAsync();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        SpringTo(1f);
        PlayButton.Opacity = 0;
        _over = false;
        Tilt(0, 0);
        Fade(LensView, 0);
    }

    // The card leans towards the pointer, and the lens follows it across the picture.
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!Ui.Effects || Face.ActualWidth <= 0) return;
        var on = e.GetCurrentPoint(Face).Position;
        Tilt((float)(on.X / Face.ActualWidth * 2 - 1), (float)(on.Y / Face.ActualHeight * 2 - 1));
        _pointer = e.GetCurrentPoint(PictureArea).Position;
        LensView.Invalidate();
    }

    // The card lifts a little under the pointer and settles back with a slight bounce.
    private void SpringTo(float scale)
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        visual.CenterPoint = new Vector3((float)ActualWidth / 2, (float)ActualHeight / 2, 0);
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.FinalValue = new Vector3(scale);
        spring.DampingRatio = 0.55f;
        spring.Period = TimeSpan.FromMilliseconds(50);
        visual.StartAnimation("Scale", spring);
    }

    // Depth for the lean, set on the card so its face turns in three dimensions about its middle, nearer edge larger.
    private void Perspective()
    {
        var (x, y) = ((float)ActualWidth / 2, (float)ActualHeight / 2);
        var depth = Matrix4x4.Identity;
        depth.M34 = -1f / 600;
        ElementCompositionPreview.GetElementVisual(this).TransformMatrix = Matrix4x4.CreateTranslation(-x, -y, 0) * depth * Matrix4x4.CreateTranslation(x, y, 0);
    }

    /// <param name="x">Where the pointer is across the card, -1 at its left edge to 1 at its right; 0 and 0 to stand straight.</param>
    private void Tilt(float x, float y)
    {
        var face = ElementCompositionPreview.GetElementVisual(Face);
        face.CenterPoint = new Vector3((float)Face.ActualWidth / 2, (float)Face.ActualHeight / 2, 0);
        // Turned about the line across the card at right angles to the pointer's direction, so the side nearest it dips away.
        if (x != 0 || y != 0) face.RotationAxis = new Vector3(-y, x, 0);
        var turn = face.Compositor.CreateSpringScalarAnimation();
        turn.FinalValue = 9 * MathF.Min(1, MathF.Sqrt(x * x + y * y));
        turn.DampingRatio = 0.7f;
        turn.Period = TimeSpan.FromMilliseconds(45);
        face.StartAnimation("RotationAngleInDegrees", turn);
    }

    private static void Fade(UIElement element, float to)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, to);
        fade.Duration = TimeSpan.FromMilliseconds(180);
        visual.StartAnimation("Opacity", fade);
    }

    // ---- The lens ----

    private async Task ShowLensAsync()
    {
        var path = Card?.ThumbnailPath;
        if (string.IsNullOrEmpty(path)) return;
        if (path != _pictureOf || _picture is null)
        {
            _pictureOf = path;
            try
            {
                using var full = await CanvasBitmap.LoadAsync(LensView, path);
                if (_pictureOf != path) return; // the card has been given another clip meanwhile
                // At the size it is shown, in real pixels, so it stays sharp on a high-resolution screen.
                var scale = LensView.Dpi / 96;
                var (w, h) = ((float)(PictureArea.ActualWidth * scale), (float)(PictureArea.ActualHeight * scale));
                _picture?.Dispose();
                _picture = new CanvasRenderTarget(LensView, w, h, 96);
                var fit = Math.Max(w / full.Size.Width, h / full.Size.Height);
                var (fw, fh) = (full.Size.Width * fit, full.Size.Height * fit);
                using (var session = _picture.CreateDrawingSession())
                    session.DrawImage(full, new Rect((w - fw) / 2, (h - fh) / 2, fw, fh), full.Bounds);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                (_picture, _pictureOf) = (null, null); // no lens; the picture is shown as it is
                return;
            }
        }
        LensView.Invalidate();
        Fade(LensView, 1);
    }

    // The picture, and over it a circle of glass at the pointer: the picture swells beneath it, with a soft highlight up
    // and to the left where the light catches the glass and a thin bright rim. Drawn in the card's own measure.
    private void OnDrawLens(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_picture is null || !_over) return;
        const float Radius = 34, Swell = 1.3f;
        var (session, source, whole) = (args.DrawingSession, _picture.Bounds, new Rect(0, 0, sender.ActualWidth, sender.ActualHeight));
        var at = new Vector2((float)_pointer.X, (float)_pointer.Y);
        session.DrawImage(_picture, whole, source);
        using (var glass = CanvasGeometry.CreateCircle(session, at, Radius))
        using (session.CreateLayer(1, glass))
        {
            var swollen = new Rect(at.X - at.X * Swell, at.Y - at.Y * Swell, whole.Width * Swell, whole.Height * Swell);
            session.DrawImage(_picture, swollen, source);
            using var shine = new CanvasRadialGradientBrush(session, Windows.UI.Color.FromArgb(77, 255, 255, 255), Windows.UI.Color.FromArgb(0, 255, 255, 255))
            {
                Center = at - new Vector2(Radius * 0.35f),
                RadiusX = Radius * 0.5f,
                RadiusY = Radius * 0.5f,
            };
            session.FillCircle(at - new Vector2(Radius * 0.35f), Radius * 0.5f, shine);
        }
        session.DrawCircle(at, Radius, Windows.UI.Color.FromArgb(56, 255, 255, 255), 1.5f);
    }
}
