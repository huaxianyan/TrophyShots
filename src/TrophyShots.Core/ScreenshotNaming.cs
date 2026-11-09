using System.IO;

namespace TrophyShots.Core;

/// <summary>
/// 截图落点规则，来源是 mpv 自己的 <c>screenshot-directory</c> / <c>screenshot-template</c> / <c>screenshot-format</c>，
/// 这样工具截出来的文件名和用户手动用 mpv 截的一致。
/// </summary>
public sealed record ScreenshotNaming(string Directory, string Template, string Format = "png")
{
    /// <summary>没有 mpv 配置时的默认：录像同目录的 <c>screenshots/&lt;主文件名&gt;.png</c>。</summary>
    public static ScreenshotNaming Default { get; } = new("screenshots", "%F");

    /// <summary>
    /// 算出这条录像的截图应该落在哪。模板里出现无法预计算的占位符（时间、计数一类）时返回 null，
    /// 表示"只有 mpv 自己知道文件名"，此时只能用落盘后探测。
    /// </summary>
    public string? TryResolveTarget(string videoPath, string? mpvConfigDirectory = null)
    {
        string videoFull = Path.GetFullPath(videoPath);
        string? name = ExpandTemplate(videoFull);
        if (name is null)
        {
            return null;
        }

        string directory = ResolveDirectory(Path.GetDirectoryName(videoFull)!, mpvConfigDirectory);
        return Path.Combine(directory, name + "." + Format);
    }

    /// <summary>截图目录（模板算不出文件名时也需要知道落在哪个目录）。</summary>
    public string ResolveDirectoryFor(string videoPath, string? mpvConfigDirectory = null) =>
        ResolveDirectory(Path.GetDirectoryName(Path.GetFullPath(videoPath))!, mpvConfigDirectory);

    /// <summary>模板展开：<c>%f</c> = 带扩展名的文件名，<c>%F</c> = 去掉扩展名，<c>%%</c> = 字面百分号。</summary>
    public string? ExpandTemplate(string videoPath)
    {
        string fileName = Path.GetFileName(videoPath);
        string baseName = Path.GetFileNameWithoutExtension(videoPath);
        var result = new System.Text.StringBuilder(Template.Length + 16);

        for (int i = 0; i < Template.Length; i++)
        {
            char current = Template[i];
            if (current != '%')
            {
                result.Append(current);
                continue;
            }

            if (i + 1 >= Template.Length)
            {
                return null;
            }

            char token = Template[++i];
            switch (token)
            {
                case 'f':
                    result.Append(fileName);
                    break;
                case 'F':
                    result.Append(baseName);
                    break;
                case '%':
                    result.Append('%');
                    break;
                default:
                    // %n（计数）、%p（时间点）、%# 等需要 mpv 运行期才知道，预计算不了。
                    return null;
            }
        }

        return result.ToString();
    }

    private string ResolveDirectory(string videoDirectory, string? mpvConfigDirectory)
    {
        string directory = Directory.Trim().Trim('"');

        if (directory.Length == 0)
        {
            return videoDirectory;
        }

        if (directory.StartsWith("~~/", StringComparison.Ordinal) && mpvConfigDirectory is not null)
        {
            directory = Path.Combine(mpvConfigDirectory, directory[3..]);
        }
        else if (directory.StartsWith("~~/", StringComparison.Ordinal) || directory is "~~")
        {
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mpv");
        }
        else if (directory.StartsWith("~/", StringComparison.Ordinal) || directory is "~")
        {
            directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                directory.Length > 2 ? directory[2..] : string.Empty);
        }

        return Path.IsPathRooted(directory) ? directory : Path.Combine(videoDirectory, directory);
    }

    public string Describe() => $"{Directory} / {Template} / {Format}";
}
