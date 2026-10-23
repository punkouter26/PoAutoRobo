using Windows.UI;

namespace PoAutoRobo.App.Views;

/// <summary>Small sums on colours that more than one drawing does.</summary>
internal static class Colours
{
    /// <summary>The colour at an opacity from 0 to 1; anything outside is taken as the nearest end.</summary>
    public static Color Faded(Color c, float alpha) => Color.FromArgb((byte)(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);
}
