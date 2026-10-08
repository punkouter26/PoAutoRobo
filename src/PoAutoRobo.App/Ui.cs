using Microsoft.UI.Xaml;

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

    public static bool Not(bool on) => !on;
}
