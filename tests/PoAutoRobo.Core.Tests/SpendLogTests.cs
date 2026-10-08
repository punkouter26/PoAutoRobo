
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

        // Script and voice use has no price the app knows; it is counted, and adds nothing to the dollars.
        SpendLog.Add(episode, "script tokens", 0, units: 1200);
        SpendLog.Add(episode, "script tokens", 0, units: 300);
        SpendLog.Add(episode, "voice characters", 0, units: 90);
        Assert.Equal(1500, SpendLog.Units(episode, "script tokens"));
        Assert.Equal(90, SpendLog.Units(episode, "voice characters"));
        Assert.Equal(0.06m, SpendLog.Total(episode));
    }
}
