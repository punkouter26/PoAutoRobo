using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;

namespace PoAutoRobo.App.Views;

public sealed partial class InspectorView : UserControl
{
    private readonly MediaPlayer _player = new();
    private MainViewModel? _viewModel;

    public InspectorView() => InitializeComponent();

    public MainViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            if (value is not null)
                value.PlayAudio = Play;
            Bindings.Update();
        }
    }

    private void Play(string path)
    {
        _player.Source = MediaSource.CreateFromUri(new Uri(path));
        _player.Play();
    }

    private void OnVideoDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnVideoDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count > 0)
            ViewModel?.AttachVideoCommand.Execute(items[0].Path);
    }

    private async void OnPickVideo(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var extension in PoAutoRobo.Core.Pipeline.EpisodeBuilder.VideoExtensions)
            picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle); // a desktop app must say which window owns the dialog
        if (await picker.PickSingleFileAsync() is { } file)
            ViewModel?.AttachVideoCommand.Execute(file.Path);
    }

    // Applied when the box loses focus, not per keystroke: each apply can ask the model whether the picture still fits.
    private void OnDialogueLostFocus(object sender, RoutedEventArgs e) => ViewModel?.ApplyDialogueCommand.Execute(null);
}
