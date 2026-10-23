using System.Collections.Concurrent;
using System.Numerics;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace PoAutoRobo.App.Views;

/// <summary>
/// The deck's backdrop: a fluid of light in the accent colour that really flows (see <see cref="FluidCarry"/> and the
/// steps beside it). The pointer stirs it as it crosses the deck, a card dragged through it pulls a wake, and a clip
/// that is done sends out a ring of swirls. Three slow currents keep it moving when nothing else does. Drawn only while
/// the deck is showing and the app is in front; Windows' switch for no animation leaves it out altogether.
/// </summary>
/// <remarks>
/// The fluid lives on a grid about 150 cells tall whatever the window's size, so it costs the same on a large screen,
/// and is drawn smoothly enlarged. Win2D's animated canvas draws on a thread of its own: the page hands it pushes
/// through a queue, and everything else here belongs to that thread.
/// </remarks>
public sealed partial class FluidBackdrop : UserControl
{
    private const int Rows = 150, Rounds = 24; // grid height in cells; rounds of settling the pressure a frame
    private const float Step = 1 / 30f;

    private readonly CanvasAnimatedControl _canvas = new() { ClearColor = Colors.Transparent, IsHitTestVisible = false, IsTabStop = false };
    private readonly ConcurrentQueue<Push> _pushes = new();
    private readonly Queue<Push> _seeds = new(); // the drawing thread's own, in cells
    private bool _wanted, _inFront = true, _loaded, _light;
    private Color _accent;

    // The drawing thread's own.
    private CanvasRenderTarget? _moving, _movingNext, _ink, _inkNext, _spin, _spread, _pressure, _pressureNext, _seen;
    private PixelShaderEffect<FluidCarry>? _carry, _carryInk; // the movement's and the colour's, each with its own inputs
    private PixelShaderEffect<FluidSpin>? _spinStep;
    private PixelShaderEffect<FluidWhirl>? _whirl;
    private PixelShaderEffect<FluidSpread>? _spreadStep;
    private PixelShaderEffect<FluidPressure>? _settlePressure;
    private PixelShaderEffect<FluidSettle>? _settle;
    private PixelShaderEffect<FluidLook>? _look;
    private Vector2 _grid;
    private float _time;

    /// <summary>A push on the fluid, in the backdrop's own units: where, which way and how hard, and whether it brings colour.</summary>
    private readonly record struct Push(Vector2 At, Vector2 Speed, float Width, float Ink, int Hue);

    public FluidBackdrop()
    {
        Content = _canvas;
        IsHitTestVisible = false;
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        _canvas.TargetElapsedTime = TimeSpan.FromSeconds(Step);
        _canvas.Draw += OnDraw;
        _canvas.CreateResources += (_, _) => Forget(); // a lost graphics device takes the grids with it; they are made again
        Loaded += (_, _) =>
        {
            _loaded = true;
            Colours();
            if (!new UISettings().AnimationsEnabled) Visibility = Visibility.Collapsed;
            Pace();
        };
        ActualThemeChanged += (_, _) => Colours();
        Unloaded += (_, _) =>
        {
            _loaded = false;
            _canvas.RemoveFromVisualTree(); // Win2D controls must be detached explicitly or they leak
        };
    }

    /// <summary>True while the page it is behind is the one showing.</summary>
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

    /// <summary>The pointer, or a card being dragged, has moved through the fluid from one point to another, in the backdrop's units.</summary>
    public void Stir(Point from, Point to, bool dragging)
    {
        var (a, b) = (new Vector2((float)from.X, (float)from.Y), new Vector2((float)to.X, (float)to.Y));
        var speed = (b - a) / Step;
        if (speed.LengthSquared() < 1) return;
        // A dragged card is broad and leaves colour behind it; the pointer alone moves the fluid and stains it faintly.
        _pushes.Enqueue(new Push(b, Vector2.Clamp(speed, new(-2400), new(2400)), dragging ? 60 : 34, dragging ? 0.8f : 0.3f, dragging ? 1 : 0));
    }

    /// <summary>A ring of swirls round a point, for a clip that has just been done.</summary>
    public void Burst(Point at)
    {
        var middle = new Vector2((float)at.X, (float)at.Y);
        for (var i = 0; i < 4; i++)
        {
            var angle = i * MathF.PI / 2 + 0.4f;
            var way = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            _pushes.Enqueue(new Push(middle + way * 30, new Vector2(-way.Y, way.X) * 900 + way * 500, 46, 1.2f, i % 3));
        }
    }

    private void Colours()
    {
        _accent = (Color)Application.Current.Resources["SystemAccentColor"];
        _light = ActualTheme == ElementTheme.Light;
    }

    private void Pace()
    {
        if (_loaded) _canvas.Paused = !(_wanted && _inFront);
    }

    // ---- On the drawing thread ----

    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var size = sender.Size;
        if (size.Width < 8 || size.Height < 8) return;
        var grid = new Vector2(MathF.Round(Rows * (float)(size.Width / size.Height)), Rows);
        grid.X = Math.Clamp(grid.X, 40, 480);
        if (grid != _grid || _moving is null) Make(sender, grid);
        var scale = grid.Y / (float)size.Height; // backdrop units to cells
        _time += Step;

        // This frame's pushes: up to three from the queue, the rest left for the next frames, and one of the slow currents.
        var pushes = new List<Push>(4);
        while (pushes.Count < 3 && _seeds.TryDequeue(out var seed))
            pushes.Add(seed); // already in cells
        while (pushes.Count < 3 && _pushes.TryDequeue(out var push))
            pushes.Add(push with { At = push.At * scale, Speed = push.Speed * scale, Width = push.Width * scale });
        pushes.Add(Current(grid));
        while (pushes.Count < 4) pushes.Add(default);

        // The accent and two colours partway to its neighbours round the colour wheel. Worked out in ordinary numbers: the graphics card's
        // own vector types only do arithmetic inside a shader, and give nothing here.
        var accent = new Vector3(_accent.R, _accent.G, _accent.B) / 255;
        Vector3[] hues = [accent, Vector3.Lerp(accent, new(accent.Z, accent.X, accent.Y), 0.45f), Vector3.Lerp(accent, new(accent.Y, accent.Z, accent.X), 0.3f)];
        float4 At(int i) => new(pushes[i].At.X, pushes[i].At.Y, Math.Max(pushes[i].Width, 0.1f), 0);
        float4 Moves(int i) => new(pushes[i].Speed.X * 0.12f, pushes[i].Speed.Y * 0.12f, 0, 0);
        float4 Stains(int i)
        {
            var colour = hues[pushes[i].Hue] * pushes[i].Ink;
            return new(colour.X, colour.Y, colour.Z, 0);
        }
        var cells = new float2(grid.X, grid.Y);

        // 1. The movement carries itself along, and takes the pushes.
        _carry!.ConstantBuffer = new FluidCarry(cells, Step, 0.993f, 1, At(0), Moves(0), At(1), Moves(1), At(2), Moves(2), At(3), Moves(3));
        Run(_carry, _movingNext!, _moving!, _moving!);
        (_moving, _movingNext) = (_movingNext, _moving);

        // 2. Its small whirls are strengthened.
        _spinStep!.ConstantBuffer = new FluidSpin(cells);
        Run(_spinStep, _spin!, _moving!);
        _whirl!.ConstantBuffer = new FluidWhirl(cells, Step, 12);
        Run(_whirl, _movingNext!, _moving!, _spin!);
        (_moving, _movingNext) = (_movingNext, _moving);

        // 3. It is made incompressible: the pressure is settled, then its slope taken out.
        _spreadStep!.ConstantBuffer = new FluidSpread(cells);
        Run(_spreadStep, _spread!, _moving!);
        for (var round = 0; round < Rounds; round++)
        {
            _settlePressure!.ConstantBuffer = new FluidPressure(cells, round == 0 ? 0.8f : 1);
            Run(_settlePressure, _pressureNext!, _pressure!, _spread!);
            (_pressure, _pressureNext) = (_pressureNext, _pressure);
        }
        _settle!.ConstantBuffer = new FluidSettle(cells);
        Run(_settle, _movingNext!, _pressure!, _moving!);
        (_moving, _movingNext) = (_movingNext, _moving);

        // 4. The colour is carried by the movement, fading slowly, and takes the pushes' colour.
        _carryInk!.ConstantBuffer = new FluidCarry(cells, Step, 0.996f, 0, At(0), Stains(0), At(1), Stains(1), At(2), Stains(2), At(3), Stains(3));
        Run(_carryInk, _inkNext!, _ink!, _moving!);
        (_ink, _inkNext) = (_inkNext, _ink);

        // 5. Lit and shown, enlarged smoothly to the backdrop's size.
        _look!.ConstantBuffer = new FluidLook(cells, _light ? 0.4f : 0.5f);
        Run(_look, _seen!, _ink!);
        args.DrawingSession.DrawImage(_seen, new Rect(0, 0, size.Width, size.Height), new Rect(0, 0, grid.X, grid.Y), 1, CanvasImageInterpolation.HighQualityCubic);
    }

    // One of three slow currents, taking turns: each circles the deck on its own path, pushing gently and leaving a little
    // colour. Gently, because a push is added every frame: a strong one would build without end.
    private Push Current(Vector2 grid)
    {
        var which = (int)(_time * 30) % 3;
        var t = _time * (0.09f + which * 0.025f) + which * 2.1f;
        var at = new Vector2(grid.X * (0.5f + 0.38f * MathF.Sin(t * 1.3f + which)), grid.Y * (0.5f + 0.36f * MathF.Sin(t * 1.7f + which * 1.9f)));
        var way = new Vector2(MathF.Cos(t * 1.3f + which) * 1.3f * grid.X * 0.38f, MathF.Cos(t * 1.7f + which * 1.9f) * 1.7f * grid.Y * 0.36f);
        return new Push(at, Vector2.Normalize(way + new Vector2(0.001f, 0)) * 6, 10, 0.25f, which);
    }

    /// <summary>Works out one step into <paramref name="into"/>, writing its values as they are, from the grids given.</summary>
    private static void Run<T>(PixelShaderEffect<T> step, CanvasRenderTarget into, params CanvasRenderTarget[] from)
        where T : unmanaged, ComputeSharp.D2D1.ID2D1PixelShader, ComputeSharp.D2D1.Descriptors.ID2D1PixelShaderDescriptor<T>
    {
        for (var i = 0; i < from.Length; i++)
            step.Sources[i] = from[i];
        using var session = into.CreateDrawingSession();
        session.Blend = CanvasBlend.Copy;
        session.DrawImage(step, 0, 0, new Rect(0, 0, into.SizeInPixels.Width, into.SizeInPixels.Height));
    }

    private void Make(ICanvasResourceCreator device, Vector2 grid)
    {
        Forget();
        _grid = grid;
        CanvasRenderTarget New() => new(device, grid.X, grid.Y, 96, DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
        (_moving, _movingNext, _ink, _inkNext, _spin, _spread, _pressure, _pressureNext, _seen) = (New(), New(), New(), New(), New(), New(), New(), New(), New());
        foreach (var target in new[] { _moving, _movingNext, _ink, _inkNext, _spin, _spread, _pressure, _pressureNext })
        {
            using var session = target.CreateDrawingSession();
            session.Clear(Color.FromArgb(255, 0, 0, 0)); // still, with no colour and no pressure; opaque, so nothing is ever multiplied away
        }
        (_carry, _carryInk, _spinStep, _whirl, _spreadStep, _settlePressure, _settle, _look) = (new(), new(), new(), new(), new(), new(), new(), new());
        // The steps hand each other values that go below nought and above one. Direct2D's own working buffers hold
        // neither unless told to, and a fluid whose movement can only go one way tears itself to noise.
        foreach (var step in new ICanvasEffect[] { _carry, _carryInk, _spinStep, _whirl, _spreadStep, _settlePressure, _settle, _look })
            step.BufferPrecision = CanvasBufferPrecision.Precision32Float;

        // A few clouds of colour to start from, each already turning, so the deck does not open on an empty pane.
        var random = new Random();
        for (var i = 0; i < 9; i++)
        {
            var at = new Vector2(grid.X * (0.1f + 0.8f * random.NextSingle()), grid.Y * (0.1f + 0.8f * random.NextSingle()));
            var angle = random.NextSingle() * MathF.Tau;
            var seed = new Push(at, new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 260, grid.Y * 0.12f, 2.2f, i % 3);
            _seeds.Enqueue(seed);
        }
    }

    private void Forget()
    {
        foreach (var target in new[] { _moving, _movingNext, _ink, _inkNext, _spin, _spread, _pressure, _pressureNext, _seen })
            target?.Dispose();
        _carry?.Dispose();
        _carryInk?.Dispose();
        _spinStep?.Dispose();
        _whirl?.Dispose();
        _spreadStep?.Dispose();
        _settlePressure?.Dispose();
        _settle?.Dispose();
        _look?.Dispose();
        _moving = null;
    }
}
