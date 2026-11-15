using System.Text.Json;
using Avalonia;
using InnerVoice.App.Windows;
using Xunit;

namespace InnerVoice.App.Tests;

/// Each book's window reopens where it was left (the Mac's ProjectWindowFrame).
public class WindowFrameTests
{
    [Fact]
    public void AFrameIsRestoredOnlyWhileItsScreenIsThere()
    {
        var screens = new[] { new PixelRect(0, 0, 1920, 1040) };
        Assert.True(new ProjectWindowFrame(100, 80, 1200, 800).Fits(screens));
        Assert.False(new ProjectWindowFrame(2500, 80, 1200, 800).Fits(screens));   // a display unplugged
        Assert.True(new ProjectWindowFrame(2500, 80, 1200, 800).Fits([.. screens, new PixelRect(1920, 0, 2560, 1400)]));
        Assert.False(new ProjectWindowFrame(100, 80, 50, 800).Fits(screens));      // not a real size
        // Scaled: a 125% screen's window is bigger in pixels than in its own units.
        Assert.True(new ProjectWindowFrame(-1400, 0, 1200, 800).Fits(screens, 1.25));
        Assert.False(new ProjectWindowFrame(-1400, 0, 1200, 800).Fits(screens, 1));
    }

    [Fact]
    public void FramesSurviveTheSettingsFile()
    {
        var all = new Dictionary<string, ProjectWindowFrame> { ["Placeholder|123"] = new(10, 20, 1300, 860, Maximized: true) };
        var back = JsonSerializer.Deserialize<Dictionary<string, ProjectWindowFrame>>(JsonSerializer.Serialize(all));
        Assert.Equal(all["Placeholder|123"], back!["Placeholder|123"]);
    }
}
