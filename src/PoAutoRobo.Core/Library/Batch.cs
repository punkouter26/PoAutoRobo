namespace PoAutoRobo.Core.Library;

/// <summary>Work done a few pieces at a time, which the services answer far sooner than one after another.</summary>
public static class Batch
{
    /// <summary>
    /// Runs <paramref name="work"/> on every item, at most <paramref name="atOnce"/> at a time. Each piece carries on
    /// where it was started, so pieces started on the window's thread may change what it shows. The first failure is
    /// thrown once every piece already started has finished.
    /// </summary>
    public static async Task EachAsync<T>(IEnumerable<T> items, int atOnce, Func<T, Task> work, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(atOnce);
        await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await work(item);
            }
            finally
            {
                gate.Release();
            }
        }));
    }
}
