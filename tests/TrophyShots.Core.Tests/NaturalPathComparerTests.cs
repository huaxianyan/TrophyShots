using Xunit;

using TrophyShots.Core;

namespace TrophyShots.Core.Tests;

public sealed class NaturalPathComparerTests
{
    [Theory]
    [InlineData("Replay 2026-10-22 00-01-37.mkv", "Replay 2026-10-22 00-01-42.mkv")]
    [InlineData("Replay 2.mkv", "Replay 10.mkv")]
    [InlineData("replay 2.mkv", "Replay 10.mkv")]
    [InlineData("Replay 2026-10-22 00-01-37.mkv", "Replay 2026-10-23 00-01-37.mkv")]
    public void SortsByTimeThenByNumericValue(string earlier, string later)
    {
        Assert.True(NaturalPathComparer.Instance.Compare(earlier, later) < 0, $"{earlier} 应排在 {later} 前面");
        Assert.True(NaturalPathComparer.Instance.Compare(later, earlier) > 0, $"{later} 应排在 {earlier} 后面");
    }

    [Fact]
    public void TreatsZeroPaddedNumbersAsEqualValue()
    {
        Assert.Equal(0, NaturalPathComparer.Instance.Compare("take 007.mkv", "take 7.mkv"));
    }

    [Fact]
    public void SortsDirectoryContentsInFilenameOrder()
    {
        string directory = Directory.CreateTempSubdirectory("trophyshots-sort-").FullName;
        try
        {
            foreach (string name in new[]
            {
                "Replay 2026-10-22 00-01-42.mkv",
                "Replay 2026-10-22 00-01-37.mkv",
                "notes.txt",
            })
            {
                File.WriteAllText(Path.Combine(directory, name), string.Empty);
            }

            IReadOnlyList<string> files = VideoLibrary.CollectVideoFiles(directory);

            Assert.Equal(
                ["Replay 2026-10-22 00-01-37.mkv", "Replay 2026-10-22 00-01-42.mkv"],
                files.Select(file => Path.GetFileName(file)!).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
