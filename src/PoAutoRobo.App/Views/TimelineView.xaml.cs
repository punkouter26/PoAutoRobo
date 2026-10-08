using System.ComponentModel;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PoAutoRobo.App.ViewModels;
using PoAutoRobo.Core.Pipeline;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI;

namespace PoAutoRobo.App.Views;

public sealed partial class TimelineView : UserControl
{
    private const int WaveBars = 600;

    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private IReadOnlyList<CaptionCue> _cues = [];
    private IReadOnlyList<ClipMark> _marks = [];
    private float[] _peaks = [];
    private TimeSpan _length; // of the whole preview, from the narration files
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

    // The screen is as large as fits in the space while staying 16:9.
    private void OnStageSized(object sender, SizeChangedEventArgs e)
    {
        Screen.Width = Math.Max(1, Math.Min(e.NewSize.Width, e.NewSize.Height * 16 / 9));
        Screen.Height = Screen.Width * 9 / 16;
    }

    private void Load()
    {
        var preview = ViewModel?.Preview;
        PlayButton.IsEnabled = Scrubber.IsEnabled = preview is not null;
        _marks = preview?.Marks ?? [];
        _peaks = [];
        _length = TimeSpan.Zero;
        if (preview is not null)
        {
            _player.Source = MediaSource.CreateFromUri(new Uri(preview.VideoPath));
            _ = LoadWaveAsync(preview.Marks);
        }
        Refresh();
    }

    /// <summary>Reads each clip's narration into one row of bars spanning the preview. Read off the UI thread: it is every sample of every clip.</summary>
    private async Task LoadWaveAsync(IReadOnlyList<ClipMark> marks)
    {
        try
        {
            var (peaks, length) = await Task.Run(() =>
            {
                var total = marks[^1].Start + WavInfo.Duration(marks[^1].AudioPath);
                var bars = new float[WaveBars];
                for (var i = 0; i < marks.Count; i++)
                {
                    var from = (int)(marks[i].Start / total * WaveBars);
                    var to = i + 1 < marks.Count ? (int)(marks[i + 1].Start / total * WaveBars) : WaveBars;
                    WavInfo.Peaks(marks[i].AudioPath, to - from).CopyTo(bars.AsSpan(from));
                }
                return (bars, total);
            });
            if (!ReferenceEquals(marks, _marks)) return; // a newer preview has replaced this one
            (_peaks, _length) = (peaks, length);
            Wave.Invalidate();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            // No waveform is drawn; the player and scrubber work without it.
        }
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
        var position = TimeSpan.FromSeconds(e.NewValue);
        _player.PlaybackSession.Position = position;
        TimeText.Text = $"{position:m\\:ss} / {_player.PlaybackSession.NaturalDuration:m\\:ss}"; // the clock only ticks while playing
        Overlay.Invalidate();
        Wave.Invalidate();
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
        Wave.Invalidate();
    }

    private void OnWavePressed(object sender, PointerRoutedEventArgs e)
    {
        if (_length <= TimeSpan.Zero || Wave.ActualWidth <= 0) return;
        Scrubber.Value = e.GetCurrentPoint(Wave).Position.X / Wave.ActualWidth * _length.TotalSeconds; // the scrubber moves the player
    }

    private void OnDrawWave(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_peaks.Length == 0 || _length <= TimeSpan.Zero) return;
        var (width, height) = ((float)sender.ActualWidth, (float)sender.ActualHeight);
        var session = args.DrawingSession;
        var ink = ActualTheme == ElementTheme.Light ? Colors.Black : Colors.White;
        var quiet = Color.FromArgb(90, ink.R, ink.G, ink.B);
        var accent = (Color)Application.Current.Resources["SystemAccentColor"];
        var played = (float)(_player.PlaybackSession.Position / _length) * width;

        // The bars sit in the lower part; the clip titles run along the top.
        const float TitleRoom = 16;
        var middle = TitleRoom + (height - TitleRoom) / 2;
        var step = width / _peaks.Length;
        for (var i = 0; i < _peaks.Length; i++)
        {
            var x = i * step;
            var half = Math.Max(1, _peaks[i] * (height - TitleRoom) / 2);
            session.DrawLine(x, middle - half, x, middle + half, x <= played ? accent : quiet, Math.Max(1, step - 1));
        }

        using var format = new CanvasTextFormat { FontSize = 11, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character };
        for (var i = 0; i < _marks.Count; i++)
        {
            var x = (float)(_marks[i].Start / _length) * width;
            var next = i + 1 < _marks.Count ? (float)(_marks[i + 1].Start / _length) * width : width;
            session.DrawLine(x, 0, x, height, ink, 1);
            if (next - x > 24)
                session.DrawText($"{i + 1} · {_marks[i].Title}", new Rect(x + 4, 0, next - x - 8, TitleRoom), ink, format);
        }
        session.DrawLine(played, 0, played, height, accent, 2);
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
            // The export measures font size by line height, this by letter height; 0.75 makes the preview match the
            // finished video for Segoe UI. Adjust here if the two ever look different.
            FontSize = look.FontSize * scale * 0.75f,
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
        Wave.RemoveFromVisualTree();
        _player.Dispose();
    }
}
