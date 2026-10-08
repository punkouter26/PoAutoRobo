using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;
using Windows.Storage.Pickers;

namespace PoAutoRobo.App.Views;

public sealed partial class HostSetupDialog : ContentDialog
{
    public HostSetupDialog(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        IsPrimaryButtonEnabled = Candidates.SelectedItem is string;

    private void OnUseSelected(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (Candidates.SelectedItem is string path)
            ViewModel.LockHost(path);
    }

    private async void OnPickOwn(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add(".png");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        if (await picker.PickSingleFileAsync() is { } file)
        {
            ViewModel.LockHost(file.Path);
            Hide();
        }
    }
}
