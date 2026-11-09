using System.IO;

namespace TrophyShots.Core;

/// <summary>目录浏览的纯逻辑：列子目录、上一级、磁盘列表。界面和测试都用它，避免各写一份。</summary>
public static class FolderBrowsing
{
    public static IReadOnlyList<string> Drives() => Environment.GetLogicalDrives();

    /// <summary>子目录，按自然序排列，跳过隐藏目录。</summary>
    public static IReadOnlyList<string> Subfolders(string path) =>
        Directory.EnumerateDirectories(path)
            .Where(IsVisible)
            .Order(NaturalPathComparer.Instance)
            .ToArray();

    /// <summary>上一级；已经在根目录时返回自身。</summary>
    public static string ParentOf(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrEmpty(parent) ? path : parent;
    }

    private static bool IsVisible(string path)
    {
        try
        {
            return (new DirectoryInfo(path).Attributes & FileAttributes.Hidden) == 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
