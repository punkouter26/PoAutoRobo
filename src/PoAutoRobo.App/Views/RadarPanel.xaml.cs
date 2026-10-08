using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;

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

    // Built each time it opens, so episodes created this session appear.
    private void OnSavedEpisodesOpening(object? sender, object e)
    {
        SavedEpisodesMenu.Items.Clear();
        var folders = ViewModel?.SavedEpisodes() ?? [];
        foreach (var folder in folders)
        {
            var item = new MenuFlyoutItem { Text = Path.GetFileName(folder) };
            item.Click += async (_, _) => await ViewModel!.OpenEpisodeAsync(folder);
            SavedEpisodesMenu.Items.Add(item);
        }
        if (folders.Count == 0)
            SavedEpisodesMenu.Items.Add(new MenuFlyoutItem { Text = "No saved episodes yet", IsEnabled = false });
    }

    private void OnAdoptTopic(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: PoAutoRobo.Core.Services.TopicCard card })
            ViewModel?.AdoptTopicCommand.Execute(card);
    }
}
