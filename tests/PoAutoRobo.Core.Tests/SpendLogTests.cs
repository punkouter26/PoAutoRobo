using PoAutoRobo.Core.Services;

namespace PoAutoRobo.Core.Tests;

public sealed class SpendLogTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("poautorobo-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Spend_adds_up_across_entries_and_an_episode_with_no_log_has_spent_nothing()
    {
        var episode = Path.Combine(_folder, "episode"); // not created yet: the first entry makes it

        Assert.Equal(0m, SpendLog.Total(episode));

        SpendLog.Add(episode, "3 pictures", 0.045m);
        SpendLog.Add(episode, "1 picture", 0.015m);

        Assert.Equal(0.06m, SpendLog.Total(episode));
        Assert.Equal("about $0.06 of pictures", SpendLog.InWords(SpendLog.Total(episode)));
    }
}
