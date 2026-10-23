using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;

namespace PoAutoRobo.App.Views;

/// <summary>Step 1: the intro every episode opens with and the outro it ends on, and what they are made of.</summary>
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

    /// <summary>Lets go of the video being shown, so the scratch sweep can remove it. The window calls this as it closes.</summary>
    public void Release() => _player.Dispose();

    // A new intro or outro plays as soon as it is made; leaving the page stops it.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        var made = e.PropertyName switch
        {
            nameof(MainViewModel.IntroPreviewPath) => ViewModel?.IntroPreviewPath,
            nameof(MainViewModel.OutroPreviewPath) => ViewModel?.OutroPreviewPath,
            _ => null,
        };
        if (made is not null)
        {
            _player.Source = MediaSource.CreateFromUri(new Uri(made));
            _player.Play();
        }
        else if (e.PropertyName == nameof(MainViewModel.ShowIntro) && ViewModel?.ShowIntro == false)
        {
            _player.Pause();
        }
    }

    // Each look's tile carries its look in Tag.
    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: IntroStart start }) ViewModel?.UseIntroStart(start);
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

    // ---- The small sketch of each ready-made look: where the mascot and the words sit, as FfmpegArgs draws them ----

    public static Visibility Band(IntroLayout layout) => Ui.Show(layout == IntroLayout.Lower);

    public static double MascotWidth(IntroLayout layout) => layout == IntroLayout.Centre ? 16 : 22;

    public static double MascotHeight(IntroLayout layout) => layout == IntroLayout.Centre ? 18 : 40;

    public static HorizontalAlignment MascotAcross(IntroLayout layout) => layout == IntroLayout.Centre ? HorizontalAlignment.Center : HorizontalAlignment.Right;

    public static VerticalAlignment MascotDown(IntroLayout layout) => layout == IntroLayout.Centre ? VerticalAlignment.Top : VerticalAlignment.Center;

    public static HorizontalAlignment WordsAcross(IntroLayout layout) => layout == IntroLayout.Centre ? HorizontalAlignment.Center : HorizontalAlignment.Left;

    public static VerticalAlignment WordsDown(IntroLayout layout) => layout == IntroLayout.Side ? VerticalAlignment.Center : VerticalAlignment.Bottom;
}
