using System.ComponentModel;
using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace PoAutoRobo.App.Views;

public sealed partial class RadarPanel : UserControl
{
    private MainViewModel? _viewModel;
    private bool _inFront = true;
    private Color _accent; // read on the UI thread; the backdrop is drawn on its own

    public RadarPanel() => InitializeComponent();

    public MainViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = value;
            if (value is not null) value.PropertyChanged += OnViewModelChanged;
            Bindings.Update();
            value?.RefreshTopicsCommand.Execute(null); // topics load on launch; the button reloads them
        }
    }

    /// <summary>Told by the window: false while another app is in front, when there is nobody to draw for.</summary>
    public void SetInFront(bool inFront)
    {
        _inFront = inFront;
        PaceBackdrop();
    }

    // ---- The moving backdrop ----

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _accent = (Color)Application.Current.Resources["SystemAccentColor"];
        // Windows has a switch for people who want no animation; with it off there is no backdrop at all.
        if (!new UISettings().AnimationsEnabled) Backdrop.Visibility = Visibility.Collapsed;
        Backdrop.TargetElapsedTime = TimeSpan.FromMilliseconds(33); // thirty frames a second is plenty for something this slow
        PaceBackdrop();
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => _accent = (Color)Application.Current.Resources["SystemAccentColor"];

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowTopic)) PaceBackdrop();
    }

    // Drawn only while it can be seen: this page showing, and the app in front.
    private void PaceBackdrop() => Backdrop.Paused = !(_inFront && ViewModel?.ShowTopic == true);

    // ponytail: the graphics card's own noise generator, tinted and drifting. A hand-written shader would need a
    // compiled shader file shipped with the app; write one if this ever looks too plain.
    private void OnDrawBackdrop(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var seconds = (float)args.Timing.TotalTime.TotalSeconds;
        var accent = _accent;
        var drift = new Vector2(seconds * 14, seconds * 9);
        using var cloud = new TurbulenceEffect
        {
            Size = new Vector2((float)sender.Size.Width, (float)sender.Size.Height),
            Frequency = new Vector2(0.003f),
            Octaves = 2,
            Noise = TurbulenceEffectNoise.FractalSum,
            Offset = drift,
        };
        // Every point takes the accent colour; how bright the cloud is there decides only how much of it shows.
        using var tinted = new ColorMatrixEffect
        {
            Source = cloud,
            ColorMatrix = new Matrix5x4 { M14 = 0.22f, M51 = accent.R / 255f, M52 = accent.G / 255f, M53 = accent.B / 255f },
        };
        args.DrawingSession.DrawImage(tinted, -drift); // the cloud is made at its drifted position, so it is drawn back over the page
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Backdrop.RemoveFromVisualTree(); // Win2D controls must be detached explicitly or they leak

    // ---- Saved episodes and stories ----

    private async void OnOpenEpisode(object sender, ItemClickEventArgs e)
    {
        if (ViewModel is not null && e.ClickedItem is EpisodeSummary summary)
            await ViewModel.OpenEpisodeAsync(summary.Folder);
    }

    // The menu items carry their episode in Tag.
    private static EpisodeSummary? Episode(object sender) => (sender as FrameworkElement)?.Tag as EpisodeSummary;

    private async void OnOpenFromMenu(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && Episode(sender) is { } summary)
            await ViewModel.OpenEpisodeAsync(summary.Folder);
    }

    private async void OnDuplicateEpisode(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && Episode(sender) is { } summary)
            await ViewModel.DuplicateEpisodeAsync(summary);
    }

    private void OnShowEpisode(object sender, RoutedEventArgs e)
    {
        if (Episode(sender) is { } summary)
            MainViewModel.ShowInExplorer(summary.Folder);
    }

    private async void OnDeleteEpisode(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && Episode(sender) is { } summary)
            await ViewModel.DeleteEpisodeAsync(summary);
    }

    private void OnAdoptTopic(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TopicCard card })
            ViewModel?.AdoptTopicCommand.Execute(card);
    }
}
