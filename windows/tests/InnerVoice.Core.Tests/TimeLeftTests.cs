using InnerVoice.Core;
using Xunit;

namespace InnerVoice.Core.Tests;

/// The Mac's TimeLeftTests.
public class TimeLeftTests
{
    [Fact]
    public void WaitsForEnoughToGoOn()
    {
        Assert.Null(TimeLeft.Estimate(done: 4, total: 100, elapsedSeconds: 60));
        Assert.Null(TimeLeft.Estimate(done: 10, total: 100, elapsedSeconds: 10));
        Assert.Null(TimeLeft.Estimate(done: 100, total: 100, elapsedSeconds: 600));
        Assert.Equal("…", TimeLeft.Ending(4, 100, 60));
    }

    [Fact]
    public void ScalesTheRateSoFar()
    {
        Assert.Equal("about 9 min left", TimeLeft.Estimate(done: 10, total: 100, elapsedSeconds: 60));
        Assert.Equal("about 1 hr 39 min left", TimeLeft.Estimate(done: 10, total: 1000, elapsedSeconds: 60));
        Assert.Equal("less than a minute left", TimeLeft.Text(30));
        Assert.Equal("about 2 hr left", TimeLeft.Text(7200));
        Assert.Equal(", about 9 min left…", TimeLeft.Ending(10, 100, 60));
    }
}
