using System.ComponentModel;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace PoAutoRobo.App.Views;

/// <summary>Step 1: the topic form, the saved episodes and the stories to take a topic from.</summary>
public sealed partial class TopicPage : UserControl
{
    private MainViewModel? _viewModel;
    private bool _inFront = true;
    private bool _shimmering;
    private PixelShaderEffect<Nebula>? _nebula; // made and used on the drawing thread only

    // Read by the drawing thread, set by the page.
    private Color _accent;
    private volatile bool _fetching;
    private float _energy;

    public TopicPage() => InitializeComponent();

    public MainViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelChanged;
                _viewModel.RefreshTopicsCommand.PropertyChanged -= OnRefreshChanged;
            }
            _viewModel = value;
            if (value is not null)
            {
                value.PropertyChanged += OnViewModelChanged;
                value.RefreshTopicsCommand.PropertyChanged += OnRefreshChanged;
            }
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

    // While stories are fetched the placeholder rows catch the light, and the backdrop quickens.
    private void OnRefreshChanged(object? sender, PropertyChangedEventArgs e)
    {
        var fetching = ViewModel?.RefreshTopicsCommand.IsRunning == true;
        _fetching = fetching;
        if (fetching == _shimmering) return;
        _shimmering = fetching;
        Ui.Shimmer(fetching);
    }

    // Drawn only while it can be seen: this page showing, and the app in front.
    private void PaceBackdrop() => Backdrop.Paused = !(_inFront && ViewModel?.ShowTopic == true);

    private void OnDrawBackdrop(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        _energy += ((_fetching ? 1f : 0f) - _energy) * 0.05f; // eased, so the change is felt and not seen to switch
        var (accent, size) = (_accent, sender.Size);
        _nebula ??= new PixelShaderEffect<Nebula>();
        _nebula.ConstantBuffer = new Nebula(
            (float)args.Timing.TotalTime.TotalSeconds, new float2((float)size.Width, (float)size.Height),
            new float3(accent.R / 255f, accent.G / 255f, accent.B / 255f), _energy);
        // The cloud has no edges of its own, so it is told which part of it to draw: the backdrop's own area.
        args.DrawingSession.DrawImage(_nebula, 0, 0, new Rect(0, 0, size.Width, size.Height));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Backdrop.RemoveFromVisualTree(); // Win2D controls must be detached explicitly or they leak
        _nebula?.Dispose();
        if (_shimmering) Ui.Shimmer(false);
        _shimmering = false;
    }

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
