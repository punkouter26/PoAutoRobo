using System.ComponentModel;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PoAutoRobo.App.ViewModels;
using PoAutoRobo.Core.Pipeline;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace PoAutoRobo.App.Views;

public sealed partial class TimelineView : UserControl
{
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private IReadOnlyList<CaptionCue> _cues = [];
    private MainViewModel? _viewModel;
    private bool _updatingScrubber;

    public TimelineView()
    {
        InitializeComponent();
        Player.SetMediaPlayer(_player);
        _tick.Tick += (_, _) => Refresh();
        // The length is only known once the file has opened, and that event arrives off the UI thread.
        _player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(Refresh);
    }

    public MainViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = value;
            if (value is not null)
            {
                value.PropertyChanged += OnViewModelChanged;
                value.ReleasePreview = Release;
            }
            Bindings.Update();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Preview))
            Load();
        if (e.PropertyName is nameof(MainViewModel.Preview) or nameof(MainViewModel.Captions))
        {
            _cues = ViewModel?.Preview is { } preview ? AssCaptions.Cues(preview.Segments, ViewModel.Captions) : [];
            Overlay.Invalidate();
        }
    }

    private void Load()
    {
        var hasPreview = ViewModel?.Preview is not null;
        PlayButton.IsEnabled = Scrubber.IsEnabled = hasPreview;
        if (hasPreview)
            _player.Source = MediaSource.CreateFromUri(new Uri(ViewModel!.Preview!.VideoPath));
        Refresh();
    }

    /// <summary>Lets go of the preview file so a new one can replace it.</summary>
    private void Release()
    {
        _tick.Stop();
        _player.Pause();
        _player.Source = null;
        PlayIcon.Symbol = Symbol.Play;
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        var playing = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        if (playing) _player.Pause(); else _player.Play();
        PlayIcon.Symbol = playing ? Symbol.Play : Symbol.Pause;
        if (playing) _tick.Stop(); else _tick.Start();
    }

    private void OnScrub(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingScrubber) return;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
        Overlay.Invalidate();
    }

    private void Refresh()
    {
        var session = _player.PlaybackSession;
        _updatingScrubber = true;
        Scrubber.Maximum = Math.Max(1, session.NaturalDuration.TotalSeconds);
        Scrubber.Value = session.Position.TotalSeconds;
        _updatingScrubber = false;
        TimeText.Text = $"{session.Position:m\\:ss} / {session.NaturalDuration:m\\:ss}";
        Overlay.Invalidate();
    }

    private void OnDrawCaption(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (ViewModel is null || AssCaptions.CueAt(_cues, _player.PlaybackSession.Position) is not { } cue) return;

        var style = ViewModel.Captions;
        var look = AssCaptions.LookFor(style);
        var scale = (float)(sender.ActualHeight / 1080); // caption sizes are defined on a 1080-line frame
        var accent = ViewModel.CaptionAccent;
        var first = string.Join(' ', cue.Words.Take(cue.FirstLineWords));
        var text = cue.FirstLineWords < cue.Words.Count ? first + "\n" + string.Join(' ', cue.Words.Skip(cue.FirstLineWords)) : first;

        using var format = new CanvasTextFormat
        {
            FontFamily = look.Font,
            FontSize = look.FontSize * scale,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        using var layout = new CanvasTextLayout(sender, text, format, (float)sender.ActualWidth, 0);
        var y = (float)(sender.ActualHeight - 90 * scale - layout.LayoutBounds.Height);
        var session = args.DrawingSession;

        if (look.Boxed)
        {
            var pad = look.Outline * scale + 2;
            var box = layout.LayoutBounds;
            session.FillRectangle(new Rect(box.X - pad, y + box.Y - pad, box.Width + 2 * pad, box.Height + 2 * pad), look.DarkTextOnAccent ? accent : Colors.Black);
        }
        else
        {
            using var outline = CanvasGeometry.CreateText(layout);
            session.DrawGeometry(outline, 0, y, Colors.Black, look.Outline * 2 * scale);
        }

        if (cue.Highlight >= 0)
        {
            var start = cue.Words.Take(cue.Highlight).Sum(w => w.Length + 1);
            layout.SetColor(start, cue.Words[cue.Highlight].Length, accent);
        }
        session.DrawTextLayout(layout, 0, y, look.DarkTextOnAccent ? Colors.Black : Colors.White);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _tick.Stop();
        Overlay.RemoveFromVisualTree(); // Win2D controls must be detached explicitly or they leak
    }
}
