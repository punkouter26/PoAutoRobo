namespace PoAutoRobo.App.ViewModels;

/// <summary>One entry in a drop-down list: the words shown and the value they stand for, kept together so the two cannot drift apart.</summary>
public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label; // what the list shows
}
