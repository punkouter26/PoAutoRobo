using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;
using Windows.Foundation;
using Windows.UI;

namespace PoAutoRobo.App.Views;

/// <summary>
/// While a preview or the master video is drawn: a strip of film, one frame for each clip, each developing like a
/// print in the tray as its clip is drawn: dark under the red safelight at first, coming up as the clip is drawn, and
/// flashing white as it is fixed.
/// </summary>
public sealed partial class DarkroomStrip : UserControl
{
    private const float FrameWidth = 96, FrameHeight = 54, Gap = 6;

    private readonly CanvasControl _canvas = new() { ClearColor = Colors.Transparent, IsHitTestVisible = false, IsTabStop = false, Height = FrameHeight };
    private readonly FrameClock _clock;
    private readonly DispatcherTimer _look = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<string, CanvasRenderTarget?> _pictures = []; // each clip's picture, made small; null while it loads or when it will not
    private readonly List<ColorMatrixEffect> _prints = [];                  // one for each frame
    private Frame[] _frames = [];
    private float[] _shown = [], _fixedAt = [];

    // What the strip shows of one clip, copied from its card.
    private sealed record Frame(string Title, string Picture, double Progress);

    public DarkroomStrip()
    {
        Content = _canvas;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        _clock = new FrameClock((_, _) => _canvas.Invalidate());
        _canvas.Draw += OnDraw;
        _look.Tick += (_, _) => Look();
        Unloaded += (_, _) =>
        {
            _look.Stop();
            _clock.Stop();
            _canvas.RemoveFromVisualTree(); // Win2D controls must be detached explicitly or they leak
        };
    }

    public MainViewModel? ViewModel { get; set; }

    /// <summary>Runs while clips are being drawn, and stops after.</summary>
    public void Run(bool on)
    {
        on &= Ui.Effects;
        Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) _look.Start(); else _look.Stop();
        if (on) _clock.Start(); else _clock.Stop();
        Look();
    }

    // Copies how far each clip has got, and loads the pictures not yet loaded.
    private void Look()
    {
        if (ViewModel is null) return;
        var frames = ViewModel.Clips.Select(c => new Frame(c.Clip.Title, c.ThumbnailPath, c.Progress / 100)).ToArray();
        foreach (var path in frames.Select(f => f.Picture).Where(p => p.Length > 0 && !_pictures.ContainsKey(p)))
        {
            _pictures[path] = null;
            _ = LoadAsync(path);
        }
        _frames = frames;
    }

    private async Task LoadAsync(string path)
    {
        try
        {
            using var full = await CanvasBitmap.LoadAsync(_canvas, path);
            // Kept small: a strip of postage stamps needs no more, and a dozen full pictures would fill the graphics card.
            var small = new CanvasRenderTarget(_canvas, FrameWidth * 2, FrameHeight * 2, 96);
            using (var session = small.CreateDrawingSession())
                session.DrawImage(full, Fill(full.Size, new Rect(0, 0, FrameWidth * 2, FrameHeight * 2)), full.Bounds);
            _pictures[path] = small;
        }
        catch (InvalidOperationException)
        {
            _pictures.Remove(path); // the strip had no graphics card to load onto yet; tried again on the next look
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // Not loaded: the frame shows the clip's title instead.
        }
    }

    // A rectangle the picture fills, cropped at its middle, keeping its shape.
    private static Rect Fill(Size picture, Rect frame)
    {
        var scale = Math.Max(frame.Width / picture.Width, frame.Height / picture.Height);
        var (w, h) = (picture.Width * scale, picture.Height * scale);
        return new Rect(frame.X + (frame.Width - w) / 2, frame.Y + (frame.Height - h) / 2, w, h);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var frames = _frames;
        var time = _clock.Time;
        if (_shown.Length != frames.Length) (_shown, _fixedAt) = (new float[frames.Length], Enumerable.Repeat(-1f, frames.Length).ToArray());
        while (_prints.Count < frames.Length) _prints.Add(new ColorMatrixEffect());

        var width = (float)Math.Min(FrameWidth, (sender.Size.Width - Gap * (frames.Length - 1)) / Math.Max(1, frames.Length));
        using var format = new CanvasTextFormat { FontSize = 10, WordWrapping = CanvasWordWrapping.Wrap, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
        var session = args.DrawingSession;
        for (var i = 0; i < frames.Length; i++)
        {
            var target = (float)Math.Clamp(frames[i].Progress, 0, 1);
            _shown[i] += (target - _shown[i]) * 0.12f;
            if (target >= 1 && _fixedAt[i] < 0) _fixedAt[i] = time;
            if (target < 1) _fixedAt[i] = -1;
            var flash = _fixedAt[i] < 0 ? 0 : MathF.Exp(-(time - _fixedAt[i]) * 6) * 0.85f;
            var developed = _fixedAt[i] < 0 ? _shown[i] : Math.Max(_shown[i], 1 - MathF.Exp(-(time - _fixedAt[i]) * 5));

            var frame = new Rect(i * (width + Gap), 0, width, FrameHeight);
            using var print = new CanvasCommandList(sender);
            using (var paper = print.CreateDrawingSession())
            {
                if (_pictures.GetValueOrDefault(frames[i].Picture) is { } picture)
                {
                    using (paper.CreateLayer(1, frame))
                        paper.DrawImage(picture, Fill(picture.Size, frame), picture.Bounds);
                }
                else
                {
                    // A clip with no picture of its own is a title card: its title, small, on dark ground.
                    paper.FillRectangle(frame, Color.FromArgb(255, 16, 24, 40));
                    paper.DrawText($"{i + 1} · {frames[i].Title}", new Rect(frame.X + 3, frame.Y + 2, frame.Width - 6, frame.Height - 4), Colors.White, format);
                }
            }
            _prints[i].Source = print;
            _prints[i].ColorMatrix = Developing(developed, flash);
            session.DrawImage(_prints[i], (float)frame.X, (float)frame.Y, frame);
            session.DrawRectangle(frame, Color.FromArgb(90, 0, 0, 0), 1); // the frame's edge, as on a contact sheet
        }
    }
    // The paper before anything has come up on it: a dark red, as it looks under the safelight.
    private static readonly System.Numerics.Vector3 Paper = new(0.14f, 0.02f, 0.015f);

    /// <summary>
    /// A print <paramref name="developed"/> of the way up (0 to 1): the paper giving way to the picture, everything red
    /// under the safelight until it is nearly done, then <paramref name="flash"/> of white light as it is fixed.
    /// </summary>
    private static Matrix5x4 Developing(float developed, float flash)
    {
        var safe = 1 - developed * developed * developed;
        var tint = new System.Numerics.Vector3(1, 1 - 0.55f * safe, 1 - 0.62f * safe); // the safelight, fading as it develops
        var keep = (1 - flash) * developed * tint;
        var ground = (1 - flash) * (1 - developed) * tint * Paper + new System.Numerics.Vector3(flash);
        return new Matrix5x4
        {
            M11 = keep.X,
            M22 = keep.Y,
            M33 = keep.Z,
            M44 = 1,
            M51 = ground.X,
            M52 = ground.Y,
            M53 = ground.Z,
        };
    }
}
