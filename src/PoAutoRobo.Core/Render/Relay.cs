namespace PoAutoRobo.Core.Render;

/// <summary>Reports on the calling thread; <see cref="Progress{T}"/> would post to a context and reorder values.</summary>
internal sealed class Relay<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
