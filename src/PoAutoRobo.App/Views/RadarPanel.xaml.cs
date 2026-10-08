using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;
using PoAutoRobo.Core.Services;

namespace PoAutoRobo.App.Views;

public sealed partial class RadarPanel : UserControl
{
    private MainViewModel? _viewModel;

    public RadarPanel() => InitializeComponent();

    public MainViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Bindings.Update();
            value?.RefreshTopicsCommand.Execute(null); // topics load on launch; the button reloads them
        }
    }

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

    private void OnDuplicateEpisode(object sender, RoutedEventArgs e)
    {
        if (Episode(sender) is { } summary)
            ViewModel?.DuplicateEpisode(summary);
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
