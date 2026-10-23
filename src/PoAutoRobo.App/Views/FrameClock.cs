using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace PoAutoRobo.App.Views;

/// <summary>
/// Calls back once for each frame the window draws, while it runs, with the seconds since it was made and since the
/// last frame. For the drawings that move over other things: Win2D's own animated canvas shows nothing of what lies
/// beneath it, so they are plain canvases drawn again on each of the window's frames instead.
/// </summary>
internal sealed class FrameClock(Action<float, float> tick)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;

    public bool Running { get; private set; }

    /// <summary>Seconds since the clock was made.</summary>
    public float Time => (float)_clock.Elapsed.TotalSeconds;

    public void Start()
    {
        if (Running) return;
        Running = true;
        _last = _clock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var step = (float)Math.Min(now - _last, 0.05); // a long stall is taken as one ordinary frame, so nothing leaps
        _last = now;
        tick(Time, step);
    }
}
