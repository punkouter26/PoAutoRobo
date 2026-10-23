using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PoAutoRobo.App.ViewModels;
using Windows.Storage.Pickers;

namespace PoAutoRobo.App.Views;

/// <summary>
/// How the series and this episode look, beyond the intro, as three tabs: the music and look new episodes start with,
/// the host who presents every episode, and this episode's pictures.
/// </summary>
public sealed partial class SeriesDialog : ContentDialog
{
    /// <summary>The tabs, in order, for opening the dialog on one.</summary>
    public enum Tab { Series, Host, Episode }

    private static Choice<Look>[] Looks => MainViewModel.LookChoices;

    public SeriesDialog(MainViewModel viewModel, Tab tab = Tab.Series)
    {
        ViewModel = viewModel;
        InitializeComponent();
        EpisodeLookBox.ItemsSource = Looks;
        ShowEpisode();
        Tabs.SelectedIndex = (int)tab;
    }

    public MainViewModel ViewModel { get; }

    // The buttons carry the file's role in Tag: here, only the music.
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

    private void OnTab(object sender, SelectionChangedEventArgs e)
    {
        SeriesPanel.Visibility = Ui.ShowIf(Tabs.SelectedIndex, (int)Tab.Series);
        HostPanel.Visibility = Ui.ShowIf(Tabs.SelectedIndex, (int)Tab.Host);
        EpisodePanel.Visibility = Ui.ShowIf(Tabs.SelectedIndex, (int)Tab.Episode);
        if (Tabs.SelectedIndex == (int)Tab.Episode) ShowEpisode(); // a look given on the New episodes tab may have changed it
    }

    // ---- The host ----

    private void OnCandidatePicked(object sender, SelectionChangedEventArgs e) => UseHostButton.IsEnabled = Candidates.SelectedItem is string;

    private void OnUseHost(object sender, RoutedEventArgs e)
    {
        if (Candidates.SelectedItem is not string path) return;
        ViewModel.LockHost(path);
        HostDoneText.Visibility = Visibility.Visible;
    }

    private async void OnPickOwnHost(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add(".png");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        if (await picker.PickSingleFileAsync() is not { } file) return;
        ViewModel.LockHost(file.Path);
        HostDoneText.Visibility = Visibility.Visible;
    }

    // ---- This episode's pictures ----

    // Shown as the episode has them now. Only what is changed here is applied: dealing the mix again would undo the
    // script's own choice of picture types.
    private void ShowEpisode()
    {
        var has = ViewModel.HasEpisode;
        NoEpisodeText.Visibility = Ui.Hide(has);
        EpisodeControls.Visibility = Ui.Show(has);
        if (!has) return;
        EpisodeLookBox.SelectedIndex = Math.Max(0, Array.FindIndex(Looks, choice => choice.Value == ViewModel.Look));
        HostSwitch.IsOn = ViewModel.HostMostlyVisible;
        (StillSlider.Value, PanelsSlider.Value, TitleSlider.Value) = (ViewModel.Mix.Still, ViewModel.Mix.MultiPanel, ViewModel.Mix.TitleCard);
        AppliedText.Text = "";
        ShowTotal();
    }

    private MixPercentages Mix => new((int)StillSlider.Value, (int)PanelsSlider.Value, (int)TitleSlider.Value);

    private void OnMixChanged(object sender, RangeBaseValueChangedEventArgs e) => ShowTotal();

    private void ShowTotal()
    {
        if (TotalText is null) return; // sliders report changes while the dialog is still being built
        var total = Mix.Still + Mix.MultiPanel + Mix.TitleCard;
        TotalText.Text = total == 100 ? "Total 100%" : $"Total {total}%. The three must add up to 100.";
        ApplyMixButton.IsEnabled = total == 100;
    }

    private void OnApplyMix(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasEpisode) return;
        var look = (EpisodeLookBox.SelectedItem as Choice<Look>)?.Value ?? ViewModel.Look;
        ViewModel.ApplyLook(look);
        if (HostSwitch.IsOn != ViewModel.HostMostlyVisible) ViewModel.ShowHostEverywhere(HostSwitch.IsOn);
        if (Mix != ViewModel.Mix) ViewModel.ApplyMix(Mix);
        AppliedText.Text = "Applied. Undo puts it back.";
    }

    private void OnReroll(object sender, RoutedEventArgs e)
    {
        ViewModel.RerollMix();
        AppliedText.Text = "Dealt again. Undo puts it back.";
    }
}
