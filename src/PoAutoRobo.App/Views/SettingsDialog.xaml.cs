using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;

namespace PoAutoRobo.App.Views;

/// <summary>
/// The choices the app remembers between runs: picture quality, the monthly budget and what has been spent, the
/// advanced controls, and the optional services.
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        viewModel.RefreshSpendDetail();
        InitializeComponent();
        // The model names are advanced controls, and the switch for those is on this page too.
        Services.Advanced = viewModel.Advanced;
        viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelChanged;
        LiveText.Text = !viewModel.IsConnected ? "Still connecting."
            : viewModel.OfflineMessage is null ? "Script, voice and pictures are live."
            : "Script, voice and pictures are on stand-ins, as there is no connection to Azure.";
        if (ServicesViewModel.SetOutside is { Length: > 0 } outside)
        {
            OutsideText.Text = $"Set on this computer, and so used whatever the switches say: {outside}.";
            OutsideText.Visibility = Visibility.Visible;
        }
    }

    public MainViewModel ViewModel { get; }

    public ServicesViewModel Services { get; } = new();

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Advanced)) Services.Advanced = ViewModel.Advanced;
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        Hide();
        App.Restart();
    }
}
