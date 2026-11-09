using Xunit;

using TrophyShots.Core;

namespace TrophyShots.Core.Tests;

public sealed class MpvConfigReaderTests
{
    [Fact]
    public void ReadsScreenshotKeysAndIgnoresTheRest()
    {
        string[] lines =
        [
            "# 注释",
            "hwdec=auto-safe",
            @"screenshot-directory=""F:\Video\截图""",
            "  screenshot-template = %f  ",
            "screenshot-format=png",
            "",
            "[section]",
            "screenshot-template=%F",
        ];

        IReadOnlyDictionary<string, string> settings = MpvConfigReader.Parse(lines);

        Assert.Equal(@"F:\Video\截图", settings["screenshot-directory"]);
        // 节标题之后的 %F 属于 profile，不参与解析。
        Assert.Equal("%f", settings["screenshot-template"]);
        Assert.Equal("png", settings["screenshot-format"]);
        Assert.False(settings.ContainsKey("hwdec"));
    }

    [Fact]
    public void KeysOutsideScreenshotSetAreDropped()
    {
        IReadOnlyDictionary<string, string> settings = MpvConfigReader.Parse(["hwdec=auto-safe"]);

        Assert.Empty(settings);
    }
}
