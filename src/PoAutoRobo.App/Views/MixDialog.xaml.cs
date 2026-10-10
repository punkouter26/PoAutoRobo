using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PoAutoRobo.App.ViewModels;

namespace PoAutoRobo.App.Views;

public sealed partial class MixDialog : ContentDialog
{
    // Each look with the words shown for it, kept together so the list cannot drift out of step with the looks there are.
    private static readonly Choice<Look>[] Looks =
    [
        new("Comic book", Look.Comic), new("Photograph", Look.Photoreal), new("Flat vector", Look.FlatVector), new("Cinematic", Look.Cinematic),
    ];

    public MixDialog(MixPercentages current, Look look, bool hostVisible)
    {
        InitializeComponent();
        LookBox.ItemsSource = Looks;
        LookBox.SelectedIndex = Math.Max(0, Array.FindIndex(Looks, choice => choice.Value == look));
        HostSwitch.IsOn = hostVisible;
        StillSlider.Value = current.Still;
        PanelsSlider.Value = current.MultiPanel;
        TitleSlider.Value = current.TitleCard;
        Update();
    }

    /// <summary>The shares as currently set; only applied when they add up to 100.</summary>
    public MixPercentages Mix => new((int)StillSlider.Value, (int)PanelsSlider.Value, (int)TitleSlider.Value);

    public Look Look => (LookBox.SelectedItem as Choice<Look>)?.Value ?? Look.Comic;

    public bool HostVisible => HostSwitch.IsOn;

    private void OnChanged(object sender, RangeBaseValueChangedEventArgs e) => Update();

    private void Update()
    {
        if (TotalText is null) return; // sliders report changes while the dialog is still being built
        var total = Mix.Still + Mix.MultiPanel + Mix.TitleCard;
        TotalText.Text = total == 100 ? "Total 100%" : $"Total {total}%. The three must add up to 100.";
        IsPrimaryButtonEnabled = total == 100;
    }
}
