using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;
using Windows.Media.Core;
using Windows.Media.Playback;

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

    // Applied when the box loses focus, not per keystroke: each apply can ask the model whether the picture still fits.
    private void OnDialogueLostFocus(object sender, RoutedEventArgs e) => ViewModel?.ApplyDialogueCommand.Execute(null);
}
