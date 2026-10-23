using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;

namespace PoAutoRobo.App.Views;

/// <summary>Step 1: the intro every episode opens with, and what it is made of: the channel's name, pictures and sting.</summary>
public sealed partial class IntroPage : UserControl
{
    private readonly MediaPlayer _player = new();
    private MainViewModel? _viewModel;

    public IntroPage()
    {
        InitializeComponent();
        IntroPlayer.SetMediaPlayer(_player);
    }

    public MainViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = value;
            if (value is not null) value.PropertyChanged += OnViewModelChanged;
            Bindings.Update();
        }
    }

    /// <summary>Lets go of the intro being shown, so the scratch sweep can remove it. The window calls this as it closes.</summary>
    public void Release() => _player.Dispose();

    // A new intro plays as soon as it is made; leaving the page stops it.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IntroPreviewPath) && ViewModel?.IntroPreviewPath is { } path)
        {
            _player.Source = MediaSource.CreateFromUri(new Uri(path));
            _player.Play();
        }
        else if (e.PropertyName == nameof(MainViewModel.ShowIntro) && ViewModel?.ShowIntro == false)
        {
            _player.Pause();
        }
    }

    // The buttons carry the file's role in Tag: logo, sting or mascot.
    private async void OnPick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string role } || ViewModel is null) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = role is "sting" ? PickerLocationId.MusicLibrary : PickerLocationId.PicturesLibrary };
        foreach (var extension in role is "sting" ? MainViewModel.MusicExtensions : [".png"])
            picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle); // a desktop app must say which window owns the dialog
        if (await picker.PickSingleFileAsync() is { } file)
            ViewModel.SetSeriesFile(role, file.Path);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string role })
            ViewModel?.ClearSeriesFile(role);
    }
}
