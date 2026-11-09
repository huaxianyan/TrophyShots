using Xunit;

using TrophyShots.Core;

namespace TrophyShots.Core.Tests;

public sealed class FolderBrowsingTests
{
    [Fact]
    public void SubfoldersAreSortedNaturallyAndSkipHidden()
    {
        string root = Directory.CreateTempSubdirectory("trophyshots-browse-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "录像 10"));
            Directory.CreateDirectory(Path.Combine(root, "录像 2"));
            string hidden = Path.Combine(root, "隐藏目录");
            Directory.CreateDirectory(hidden);
            File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);
            File.WriteAllText(Path.Combine(root, "a.mkv"), string.Empty);

            IReadOnlyList<string> subfolders = FolderBrowsing.Subfolders(root);

            Assert.Equal(
                ["录像 2", "录像 10"],
                subfolders.Select(path => Path.GetFileName(path)!).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParentOfWalksUpAndStopsAtRoot()
    {
        string nested = Path.Combine("C:", "Video", "FF共鸣");
        Assert.Equal(Path.Combine("C:", "Video"), FolderBrowsing.ParentOf(nested));

        string root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        Assert.Equal(root, FolderBrowsing.ParentOf(root));
    }

    [Fact]
    public void DrivesAreListedRoots()
    {
        IReadOnlyList<string> drives = FolderBrowsing.Drives();

        Assert.NotEmpty(drives);
        Assert.All(drives, drive => Assert.EndsWith(":\\", drive));
    }
}
