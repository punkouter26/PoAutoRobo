
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
        Assert.Equal("$0.06", SpendLog.Money(SpendLog.Total(episode)));

        // Script and voice use has no price the app knows; it is counted, and adds nothing to the dollars.
        SpendLog.Add(episode, "script tokens", 0, units: 1200);
        SpendLog.Add(episode, "script tokens", 0, units: 300);
        SpendLog.Add(episode, "voice characters", 0, units: 90);
        Assert.Equal(1500, SpendLog.Units(episode, "script tokens"));
        Assert.Equal(90, SpendLog.Units(episode, "voice characters"));
        Assert.Equal(0.06m, SpendLog.Total(episode));

        // Priced script use counts towards the total, and the breakdown lists each kind once, dearest first.
        SpendLog.Add(episode, SpendLog.ScriptTokens, 0.10m, units: 500);
        Assert.Equal(0.16m, SpendLog.Total(episode));
        Assert.Equal(new SpendLine(SpendLog.ScriptTokens, 0.10m, 2000), SpendLog.Breakdown(episode)[0]);
        Assert.Equal(["script tokens", "3 pictures", "1 picture", "voice characters"], SpendLog.Breakdown(episode).Select(line => line.What));

        // The month's total covers every episode under the root, and only that month.
        SpendLog.Add(Path.Combine(_folder, "another"), SpendLog.AiVideo, 0.80m);
        Assert.Equal(0.96m, SpendLog.MonthTotal(_folder, DateTimeOffset.Now));
        Assert.Equal(0m, SpendLog.MonthTotal(_folder, DateTimeOffset.Now.AddMonths(2)));
        Assert.Equal(0m, SpendLog.MonthTotal(Path.Combine(_folder, "no-such-root"), DateTimeOffset.Now));
    }
}
