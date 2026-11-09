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
    public void MissingDirectoryIsReportedInsteadOfReturningEmptyList()
    {
        string missing = Path.Combine(Path.GetTempPath(), "trophyshots-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(() => VideoLibrary.CollectVideoFiles(missing));
    }
}
