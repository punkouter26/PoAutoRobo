using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PoAutoRobo.App.ViewModels;

namespace PoAutoRobo.App.Views;

public sealed partial class ClipCard : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(ClipViewModel), typeof(ClipCard),
        new PropertyMetadata(null, (d, _) => ((ClipCard)d).Bindings.Update())); // recycled containers get a new clip

    public ClipCard() => InitializeComponent();

    /// <summary>Thumbnail for a picture path; null (nothing drawn) when the clip has no picture.</summary>
    public static ImageSource? ToImage(string? path) =>
        path is null ? null : new BitmapImage(new Uri(path)) { DecodePixelWidth = 496 };

    public ClipViewModel? Card
    {
        get => (ClipViewModel?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }
}
