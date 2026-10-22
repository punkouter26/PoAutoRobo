using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;

namespace PoAutoRobo.App.Views;

/// <summary>The series brand kit: the channel's name, its files, the look new episodes start in, and the intro to show.</summary>
public sealed partial class SeriesDialog : ContentDialog
{
    private readonly MediaPlayer _player = new();

    public SeriesDialog(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        IntroPlayer.SetMediaPlayer(_player);
        viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelChanged;
            _player.Dispose(); // lets go of the draft, so the scratch sweep can remove it
        };
    }

    public MainViewModel ViewModel { get; }

    // A new draft plays as soon as it is made.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IntroPreviewPath) && ViewModel.IntroPreviewPath is { } path)
        {
            _player.Source = MediaSource.CreateFromUri(new Uri(path));
            _player.Play();
        }
    }

    // The buttons carry the file's role in Tag: logo, sting, music or mascot.
    private async void OnPick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string role }) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = role is "sting" or "music" ? PickerLocationId.MusicLibrary : PickerLocationId.PicturesLibrary };
        foreach (var extension in role is "sting" or "music" ? MainViewModel.MusicExtensions : [".png"])
            picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle); // a desktop app must say which window owns the dialog
        if (await picker.PickSingleFileAsync() is { } file)
            ViewModel.SetSeriesFile(role, file.Path);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string role })
            ViewModel.ClearSeriesFile(role);
    }
}
