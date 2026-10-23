using System.ComponentModel;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PoAutoRobo.App.ViewModels;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using static PoAutoRobo.App.Views.Colours;

namespace PoAutoRobo.App.Views;

/// <summary>
/// The episode laid end to end under the deck: one block for each clip, as wide as it runs, against the series'
/// three to five minutes, so the running time can be seen while the clips are shaped. Click a block to pick its clip;
/// drag it along to move the clip; with the keyboard, the arrows pick and Ctrl with the arrows moves.
/// </summary>
public sealed partial class ClipStrip : UserControl
{
    private static readonly TimeSpan Shortest = TimeSpan.FromMinutes(3), Longest = TimeSpan.FromMinutes(5);
    private const float Bar = 22, Ruler = 14; // the blocks' height, and the room for the minutes above them

    private readonly CanvasControl _canvas = new() { IsTabStop = false };
    private MainViewModel? _viewModel;
    private ClipViewModel? _dragging;
    private int _pointed = -1; // the block under the pointer, lit a little

    public ClipStrip()
    {
        Content = _canvas;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        Height = Ruler + Bar + 6;
        AutomationProperties.SetName(this, "Clips laid end to end");
        _canvas.Draw += OnDraw;
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => LetGo(e);
        PointerCaptureLost += (_, _) => _dragging = null;
        PointerExited += (_, _) => Point(-1);
        KeyDown += OnKey;
        GotFocus += (_, _) => _canvas.Invalidate();
        LostFocus += (_, _) => _canvas.Invalidate();
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree(); // Win2D controls must be detached explicitly or they leak
    }

    public MainViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelChanged;
                _viewModel.Clips.CollectionChanged -= OnClipsChanged;
            }
            _viewModel = value;
            if (value is not null)
            {
                value.PropertyChanged += OnViewModelChanged;
                value.Clips.CollectionChanged += OnClipsChanged;
            }
            _canvas.Invalidate();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Runtime) or nameof(MainViewModel.SelectedClip) or nameof(MainViewModel.Episode))
            _canvas.Invalidate();
    }

    private void OnClipsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => _canvas.Invalidate();

    private IList<ClipViewModel> Cards => _viewModel?.Clips ?? [];

    // The strip runs to five and a half minutes, or further when the episode does, so the target always shows whole.
    private double Seconds => Math.Max(Cards.Sum(c => c.Duration.TotalSeconds), Longest.TotalSeconds * 1.1);

    /// <summary>Each block's left edge and width, in this control's units.</summary>
    private IEnumerable<(ClipViewModel Card, float X, float Width)> Blocks()
    {
        var scale = (float)(ActualWidth / Seconds);
        var x = 0f;
        foreach (var card in Cards)
        {
            var width = (float)card.Duration.TotalSeconds * scale;
            yield return (card, x, width);
            x += width;
        }
    }

    private int BlockAt(double x)
    {
        var i = 0;
        foreach (var (_, left, width) in Blocks())
        {
            if (x >= left && x < left + width) return i;
            i++;
        }
        return -1;
    }

    // ---- Picking and moving ----

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var at = BlockAt(e.GetCurrentPoint(this).Position.X);
        if (at < 0 || _viewModel is null) return;
        _viewModel.SelectedClip = Cards[at];
        _dragging = Cards[at];
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var x = e.GetCurrentPoint(this).Position.X;
        Point(BlockAt(x));
        if (_dragging is null || _viewModel is null) return;
        // The clip being dragged changes place as soon as the pointer passes the middle of its neighbour.
        var from = Cards.IndexOf(_dragging);
        var blocks = Blocks().ToList();
        var to = from;
        while (to + 1 < blocks.Count && x > blocks[to + 1].X + blocks[to + 1].Width / 2) to++;
        while (to - 1 >= 0 && x < blocks[to - 1].X + blocks[to - 1].Width / 2) to--;
        if (to != from) _viewModel.MoveClip(from, to);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }

    private void LetGo(PointerRoutedEventArgs e)
    {
        _dragging = null;
        ProtectedCursor = null;
        ReleasePointerCapture(e.Pointer);
    }

    private void Point(int block)
    {
        if (block == _pointed) return;
        _pointed = block;
        ToolTipService.SetToolTip(this, block >= 0 && block < Cards.Count ? $"{Cards[block].Heading} · {Cards[block].Duration:m\\:ss}" : null);
        _canvas.Invalidate();
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (_viewModel is null || Cards.Count == 0) return;
        var at = _viewModel.SelectedClip is { } picked ? Cards.IndexOf(picked) : -1;
        var by = e.Key switch { VirtualKey.Left => -1, VirtualKey.Right => 1, _ => 0 };
        if (by == 0) return;
        var moving = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var to = Math.Clamp(at + by, 0, Cards.Count - 1);
        if (moving && at >= 0) _viewModel.MoveClip(at, to);
        else _viewModel.SelectedClip = Cards[to];
        e.Handled = true;
    }

    // ---- Drawing ----

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (Cards.Count == 0 || ActualWidth <= 0) return;
        var session = args.DrawingSession;
        var width = (float)ActualWidth;
        var ink = ActualTheme == ElementTheme.Light ? Colors.Black : Colors.White;
        var accent = (Color)Application.Current.Resources["SystemAccentColor"];
        var scale = (float)(width / Seconds);
        var total = Cards.Sum(c => c.Duration.TotalSeconds);
        var inRange = total >= Shortest.TotalSeconds && total <= Longest.TotalSeconds;

        // The series' target, three to five minutes, as a band behind the blocks, and a mark for each minute.
        var band = Color.FromArgb(40, 60, 180, 90);
        session.FillRoundedRectangle(new Rect(Shortest.TotalSeconds * scale, Ruler - 2, (Longest - Shortest).TotalSeconds * scale, Bar + 4), 3, 3, band);
        using var small = new CanvasTextFormat { FontSize = 10, WordWrapping = CanvasWordWrapping.NoWrap };
        for (var minute = 1; minute * 60 < Seconds; minute++)
        {
            var x = minute * 60 * scale;
            session.DrawLine(x, Ruler - 3, x, Ruler + Bar + 3, Faded(ink, 0.25f), 1);
            session.DrawText($"{minute}:00", x + 3, 0, Faded(ink, 0.55f), small);
        }

        using var label = new CanvasTextFormat { FontSize = 11, WordWrapping = CanvasWordWrapping.NoWrap, VerticalAlignment = CanvasVerticalAlignment.Center };
        var i = 0;
        foreach (var (card, x, w) in Blocks())
        {
            var picked = ReferenceEquals(card, _viewModel?.SelectedClip);
            var fill = picked ? accent : Faded(ink, i == _pointed ? 0.24f : 0.13f);
            var block = new Rect(x + 1, Ruler, Math.Max(1, w - 2), Bar);
            session.FillRoundedRectangle(block, 4, 4, fill);
            // A clip that runs too short or too long is marked along its top edge.
            if (card.LengthWarning.Length > 0)
                session.FillRectangle(new Rect(block.X + 2, Ruler, Math.Max(1, block.Width - 4), 3), Color.FromArgb(255, 232, 72, 85));
            if (w > 18)
                session.DrawText($"{card.Number}", new Rect(block.X + 5, block.Y, block.Width - 6, block.Height), picked ? Colors.White : Faded(ink, 0.8f), label);
            i++;
        }

        // Where the episode ends now, and how that sits against the target.
        var end = (float)(total * scale);
        session.DrawLine(end, Ruler - 4, end, Ruler + Bar + 4, inRange ? Color.FromArgb(255, 40, 160, 80) : Color.FromArgb(255, 232, 72, 85), 2);
        if (FocusState != FocusState.Unfocused)
            session.DrawRoundedRectangle(new Rect(0.5, Ruler - 3.5, width - 1, Bar + 7), 5, 5, accent, 1);
    }
}
