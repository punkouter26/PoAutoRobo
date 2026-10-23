using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace PoAutoRobo.App.Views;

/// <summary>
/// Where one moment of a teaser sits in its clip: the clip as a track, the moment as a lit window on it, dragged along
/// to choose where it starts. The arrows move it half a second, with Shift two seconds. A screen reader hears it as a
/// slider of seconds.
/// </summary>
public sealed partial class MomentStrip : RangeBase
{
    private FrameworkElement? _window; // the lit part of the track; see the strip's template in App.xaml
    private double _grabbedAt = double.NaN; // where in the window the pointer took hold, in seconds

    public static readonly DependencyProperty LengthProperty = DependencyProperty.Register(
        nameof(Length), typeof(double), typeof(MomentStrip), new PropertyMetadata(1.0, (d, _) => ((MomentStrip)d).Place()));

    public MomentStrip()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        Minimum = 0;
        SmallChange = 0.5;
        LargeChange = 2;
        SizeChanged += (_, _) => Place();
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => Let(e);
        PointerCaptureLost += (_, _) => _grabbedAt = double.NaN;
        KeyDown += OnKey;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _window = GetTemplateChild("Window") as FrameworkElement;
        Place();
    }

    /// <summary>How long the moment runs, in seconds.</summary>
    public double Length
    {
        get => (double)GetValue(LengthProperty);
        set => SetValue(LengthProperty, value);
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        Place();
    }

    protected override void OnMaximumChanged(double oldMaximum, double newMaximum)
    {
        base.OnMaximumChanged(oldMaximum, newMaximum);
        Place();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new MomentStripPeer(this);

    // The window's place and width on the track. The track runs the clip's whole length: the start can go as far
    // as the moment still fits, which is the maximum, plus the moment's own length.
    private void Place()
    {
        var span = Maximum + Length;
        if (_window is null || ActualWidth <= 0 || span <= 0) return;
        var perSecond = ActualWidth / span;
        _window.Width = Math.Max(10, Length * perSecond);
        _window.Margin = new Thickness(Math.Clamp(Value, 0, Maximum) * perSecond, 0, 0, 0);
    }

    private double SecondsAt(double x) => ActualWidth <= 0 ? 0 : x / ActualWidth * (Maximum + Length);

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var at = SecondsAt(e.GetCurrentPoint(this).Position.X);
        // Taken by the window, it keeps its hold where it was taken; a click on the track centres the window there.
        _grabbedAt = at >= Value && at <= Value + Length ? at - Value : Length / 2;
        Value = Math.Clamp(at - _grabbedAt, 0, Maximum);
        CapturePointer(e.Pointer);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (double.IsNaN(_grabbedAt)) return;
        Value = Math.Round(Math.Clamp(SecondsAt(e.GetCurrentPoint(this).Position.X) - _grabbedAt, 0, Maximum) * 10) / 10;
    }

    private void Let(PointerRoutedEventArgs e)
    {
        _grabbedAt = double.NaN;
        ProtectedCursor = null;
        ReleasePointerCapture(e.Pointer);
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var step = shift ? LargeChange : SmallChange;
        switch (e.Key)
        {
            case VirtualKey.Left or VirtualKey.Down: Value = Math.Max(Minimum, Value - step); break;
            case VirtualKey.Right or VirtualKey.Up: Value = Math.Min(Maximum, Value + step); break;
            case VirtualKey.Home: Value = Minimum; break;
            case VirtualKey.End: Value = Maximum; break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Heard as a slider: its value is where the moment starts, in seconds.</summary>
    private sealed partial class MomentStripPeer(MomentStrip owner) : RangeBaseAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

        protected override string GetClassNameCore() => nameof(MomentStrip);
    }
}
