using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace PoAutoRobo.App;

/// <summary>
/// Small questions the pages ask of a value: is it there, is it this one. Asked in the page with a function binding,
/// so the view models need no second property that only repeats a first one as a yes or no. The value asked about
/// must never be null: a page is not told when a value becomes null, and would go on showing the answer it had.
/// </summary>
public static class Ui
{
    public static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Hide(bool on) => Show(!on);

    public static Visibility ShowText(string? text) => Show(!string.IsNullOrEmpty(text));

    public static Visibility HideText(string? text) => Show(string.IsNullOrEmpty(text));

    /// <summary>Shown when a choice is on a particular entry: a step, a tab.</summary>
    public static Visibility ShowIf(int value, int wanted) => Show(value == wanted);

    /// <summary>The look of the one button on a page that is the thing to do next; a plain button when it is not that now.</summary>
    public static Style Primary(bool on) => (Style)Application.Current.Resources[on ? "AccentButtonStyle" : "DefaultButtonStyle"];

    // ---- Movement shared by the pages ----

    /// <summary>Something that has just appeared grows the last little way to its size and settles with a slight bounce.</summary>
    public static void PopIn(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3((float)element.ActualSize.X / 2, (float)element.ActualSize.Y / 2, 0);
        visual.Scale = new Vector3(0.94f);
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.FinalValue = Vector3.One;
        spring.DampingRatio = 0.6f;
        spring.Period = TimeSpan.FromMilliseconds(60);
        visual.StartAnimation("Scale", spring);
    }

    private static Storyboard? _shimmer;

    /// <summary>
    /// Starts or stops the band of light that crosses every loading placeholder. Counted, since more than one page
    /// can be loading at once: it runs while anyone has asked for it.
    /// </summary>
    public static void Shimmer(bool on)
    {
        _shimmerWanted = Math.Max(0, _shimmerWanted + (on ? 1 : -1));
        if (_shimmer is null)
        {
            var brush = (LinearGradientBrush)Application.Current.Resources["ShimmerBrush"];
            // One brush width, so the repeat lands exactly where it began and the loop has no seam.
            var sweep = new DoubleAnimation { From = 0, To = brush.EndPoint.X, Duration = TimeSpan.FromSeconds(1.4), RepeatBehavior = RepeatBehavior.Forever, EnableDependentAnimation = true };
            Storyboard.SetTarget(sweep, brush.Transform);
            Storyboard.SetTargetProperty(sweep, "X");
            _shimmer = new Storyboard { Children = { sweep } };
        }
        if (_shimmerWanted > 0) _shimmer.Begin(); else _shimmer.Stop();
    }

    private static int _shimmerWanted;
}
