using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;
using WinUIEx;

namespace PoAutoRobo.App;

public sealed partial class MainWindow : WindowEx
{
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.Confirm = ConfirmAsync;
    }

    public MainViewModel ViewModel { get; }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnVisualMix(object sender, RoutedEventArgs e)
    {
        var dialog = new Views.MixDialog(ViewModel.Mix) { XamlRoot = Content.XamlRoot };
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary: ViewModel.ApplyMix(dialog.Mix); break;
            case ContentDialogResult.Secondary: ViewModel.RerollMix(); break;
        }
    }

    private async void OnHostSetup(object sender, RoutedEventArgs e) =>
        await new Views.HostSetupDialog(ViewModel) { XamlRoot = Content.XamlRoot }.ShowAsync();

    // Instance method on purpose: the generated x:Bind code calls it through the window.
    public bool HasText(string? text) => !string.IsNullOrEmpty(text);
}
