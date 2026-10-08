using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
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
        path is null ? null : new BitmapImage(new Uri(path)) { DecodePixelWidth = 400 };

    public ClipViewModel? Card
    {
        get => (ClipViewModel?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => SpringTo(1.03f);

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => SpringTo(1f);

    // The card lifts a little under the pointer and settles back with a slight bounce.
    private void SpringTo(float scale)
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        visual.CenterPoint = new Vector3((float)ActualWidth / 2, (float)ActualHeight / 2, 0);
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.FinalValue = new Vector3(scale);
        spring.DampingRatio = 0.55f;
        spring.Period = TimeSpan.FromMilliseconds(50);
        visual.StartAnimation("Scale", spring);
    }
}
