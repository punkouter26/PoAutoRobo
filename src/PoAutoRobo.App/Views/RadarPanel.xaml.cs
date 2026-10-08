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

    private void OnAdoptTopic(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: PoAutoRobo.Core.Services.TopicCard card })
            ViewModel?.AdoptTopicCommand.Execute(card);
    }
}
