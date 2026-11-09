using Xunit;

using TrophyShots.Core;

namespace TrophyShots.Core.Tests;

public sealed class VideoLibraryTests
{
    [Theory]
    [InlineData("a.mkv", true)]
    [InlineData("a.MP4", true)]
    [InlineData("a.webm", true)]
    [InlineData("a.txt", false)]
    [InlineData("a", false)]
    public void RecognisesVideoExtensions(string fileName, bool expected) =>
        Assert.Equal(expected, VideoLibrary.IsVideoFile(fileName));

    [Fact]
    public void ScreenshotLandsInScreenshotsFolderWithSameBaseName()
    {
        string video = Path.Combine("D:", "游戏录像", "Replay 2026-10-22 00-01-37.mkv");

        string screenshot = VideoLibrary.ScreenshotPathFor(video);

        Assert.Equal(Path.Combine("D:", "游戏录像", "screenshots", "Replay 2026-10-22 00-01-37.png"), screenshot);
    }

    [Fact]
    public void ExistingScreenshotMarksVideoAsCompleted()
    {
        string directory = Directory.CreateTempSubdirectory("trophyshots-state-").FullName;
        try
        {
            string video = Path.Combine(directory, "Replay 2026-10-22 00-01-37.mkv");
            File.WriteAllText(video, string.Empty);
            Assert.False(VideoLibrary.HasScreenshot(video));

            string screenshot = VideoLibrary.ScreenshotPathFor(video);
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            File.WriteAllText(screenshot, string.Empty);

            Assert.True(VideoLibrary.HasScreenshot(video));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingDirectoryIsReportedInsteadOfReturningEmptyList()
    {
        string missing = Path.Combine(Path.GetTempPath(), "trophyshots-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(() => VideoLibrary.CollectVideoFiles(missing));
    }
}
