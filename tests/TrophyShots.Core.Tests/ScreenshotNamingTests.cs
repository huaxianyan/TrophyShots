using Xunit;

using TrophyShots.Core;

namespace TrophyShots.Core.Tests;

public sealed class ScreenshotNamingTests
{
    private const string Video = @"D:\游戏录像\Replay 2026-10-22 00-01-37.mkv";

    [Fact]
    public void DefaultNamingPutsScreenshotInScreenshotsFolderNextToVideo()
    {
        string? target = ScreenshotNaming.Default.TryResolveTarget(Video);

        Assert.Equal(@"D:\游戏录像\screenshots\Replay 2026-10-22 00-01-37.png", target);
    }

    [Fact]
    public void LowercasePercentFKeepsFileExtension() =>
        Assert.Equal(
            @"F:\Video\截图\Replay 2026-10-22 00-01-37.mkv.png",
            new ScreenshotNaming(@"F:\Video\截图", "%f").TryResolveTarget(Video));

    [Fact]
    public void UppercasePercentFDropsFileExtension() =>
        Assert.Equal(
            @"F:\Video\截图\Replay 2026-10-22 00-01-37.png",
            new ScreenshotNaming(@"F:\Video\截图", "%F").TryResolveTarget(Video));

    [Theory]
    [InlineData("%p")]
    [InlineData("shot-%#04n")]
    [InlineData("%f vs %p")]
    public void TemplatesWithRuntimePlaceholdersCannotBeResolvedInAdvance(string template) =>
        Assert.Null(new ScreenshotNaming(@"F:\Video\截图", template).TryResolveTarget(Video));

    [Fact]
    public void LiteralTextAroundTokenIsKept() =>
        Assert.Equal(
            @"F:\Video\截图\奖杯-Replay 2026-10-22 00-01-37.png",
            new ScreenshotNaming(@"F:\Video\截图", "奖杯-%F").TryResolveTarget(Video));

    [Fact]
    public void RelativeDirectoryResolvesAgainstVideoFolder() =>
        Assert.Equal(
            @"D:\游戏录像\shots\Replay 2026-10-22 00-01-37.png",
            new ScreenshotNaming("shots", "%F").TryResolveTarget(Video));
}
