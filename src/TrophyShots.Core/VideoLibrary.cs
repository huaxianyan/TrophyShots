namespace TrophyShots.Core;

/// <summary>录像目录的约定：视频文件、截图目录、状态文件都按这里的规则定位。</summary>
public static class VideoLibrary
{
    /// <summary>PLAN §6.1 认可的录像扩展名。</summary>
    public static readonly string[] VideoExtensions = [".mp4", ".mkv", ".mov", ".webm", ".m4v"];

    public const string StateFileName = ".state.json";

    public static bool IsVideoFile(string path)
    {
        string extension = Path.GetExtension(path);
        return VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>扫描目录里的录像，按文件名自然排序（"Replay 2026-10-22 00-01-37" 这类命名直接可比）。</summary>
    public static IReadOnlyList<string> CollectVideoFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"目录不存在：{directory}");
        }

        string[] files = Directory.GetFiles(directory)
            .Where(IsVideoFile)
            .Order(NaturalPathComparer.Instance)
            .ToArray();

        return files;
    }

    public static string StatePathFor(string directory) => Path.Combine(directory, StateFileName);
}
