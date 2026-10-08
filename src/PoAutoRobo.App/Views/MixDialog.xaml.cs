using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PoAutoRobo.Core.Models;

namespace PoAutoRobo.App.Views;

public sealed partial class MixDialog : ContentDialog
{
    public MixDialog(MixPercentages current)
    {
        InitializeComponent();
        StillSlider.Value = current.Still;
        PanelsSlider.Value = current.MultiPanel;
        VideoSlider.Value = current.AiVideo;
        TitleSlider.Value = current.TitleCard;
        Update();
    }

    /// <summary>The shares as currently set; only applied when they add up to 100.</summary>
    public MixPercentages Mix => new((int)StillSlider.Value, (int)PanelsSlider.Value, (int)VideoSlider.Value, (int)TitleSlider.Value);

    private void OnChanged(object sender, RangeBaseValueChangedEventArgs e) => Update();

    private void Update()
    {
        if (TotalText is null) return; // sliders report changes while the dialog is still being built
        var total = Mix.Still + Mix.MultiPanel + Mix.AiVideo + Mix.TitleCard;
        TotalText.Text = total == 100 ? "Total 100%" : $"Total {total}%. The four must add up to 100.";
        IsPrimaryButtonEnabled = total == 100;
    }
}
