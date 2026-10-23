using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PoAutoRobo.App.ViewModels;
using Windows.Storage.Pickers;

namespace PoAutoRobo.App.Views;

/// <summary>
/// How the series and this episode look, beyond the intro, as three tabs: the look new episodes start with,
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
        _showing = true;
        EpisodeLookBox.SelectedIndex = Math.Max(0, Array.FindIndex(Looks, choice => choice.Value == ViewModel.Look));
        HostSwitch.IsOn = ViewModel.HostMostlyVisible;
        _showing = false;
        (StillSlider.Value, PanelsSlider.Value, TitleSlider.Value) = (ViewModel.Mix.Still, ViewModel.Mix.MultiPanel, ViewModel.Mix.TitleCard);
        AppliedText.Text = "";
        ShowTotal();
    }

    private bool _showing; // the controls are being set to what the episode has, which is not a change to apply

    // The look and the host switch are given as soon as they change: their Apply button is with the mix, which is
    // an advanced control and so often hidden.
    private void OnLookPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_showing || !ViewModel.HasEpisode || EpisodeLookBox.SelectedItem is not Choice<Look> { Value: var look } || look == ViewModel.Look) return;
        ViewModel.ApplyLook(look);
        AppliedText.Text = "New look given. Undo puts it back.";
    }

    private void OnHostToggled(object sender, RoutedEventArgs e)
    {
        if (_showing || !ViewModel.HasEpisode || HostSwitch.IsOn == ViewModel.HostMostlyVisible) return;
        ViewModel.ShowHostEverywhere(HostSwitch.IsOn);
        AppliedText.Text = HostSwitch.IsOn ? "The host is in every clip. Undo puts it back." : "Voiceover only in every clip. Undo puts it back.";
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
        if (Mix != ViewModel.Mix) ViewModel.ApplyMix(Mix);
        AppliedText.Text = "Applied. Undo puts it back.";
    }

    private void OnReroll(object sender, RoutedEventArgs e)
    {
        ViewModel.RerollMix();
        AppliedText.Text = "Dealt again. Undo puts it back.";
    }
}
