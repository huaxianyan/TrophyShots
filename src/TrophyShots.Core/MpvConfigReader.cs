using System.IO;

namespace TrophyShots.Core;

/// <summary>
/// 从 mpv 自己的配置里读截图相关设置，让工具的产出与用户手动用 mpv 截图一致。
/// 只读这三个键，别的设置不碰（工具跑 mpv 时用 <c>--no-config</c>，用户的脚本与滤镜不会混进来）。
/// </summary>
public static class MpvConfigReader
{
    public const string PortableConfigFolderName = "portable_config";
    private const string ConfigFileName = "mpv.conf";

    private static readonly string[] ScreenshotKeys = ["screenshot-directory", "screenshot-template", "screenshot-format"];

    /// <summary>
    /// 读截图设置。顺序同 mpv：可执行文件旁的 <c>portable_config/mpv.conf</c> 优先，其次 <c>%APPDATA%\mpv\mpv.conf</c>。
    /// 找不到时返回 null，由调用方退回 <see cref="ScreenshotNaming.Default"/>。
    /// </summary>
    public static ScreenshotNaming? ReadScreenshotNaming(string mpvExecutablePath)
    {
        foreach (string configPath in CandidateConfigPaths(mpvExecutablePath))
        {
            if (!File.Exists(configPath))
            {
                continue;
            }

            IReadOnlyDictionary<string, string> settings = Parse(File.ReadAllLines(configPath));
            if (!settings.TryGetValue("screenshot-template", out string? template)
                && !settings.TryGetValue("screenshot-directory", out string? _))
            {
                return null;
            }

            settings.TryGetValue("screenshot-directory", out string? directory);
            settings.TryGetValue("screenshot-format", out string? format);

            return new ScreenshotNaming(
                string.IsNullOrWhiteSpace(directory) ? ScreenshotNaming.Default.Directory : directory,
                string.IsNullOrWhiteSpace(template) ? ScreenshotNaming.Default.Template : template,
                string.IsNullOrWhiteSpace(format) ? "png" : format);
        }

        return null;
    }

    /// <summary>mpv 的配置目录，用于展开 <c>~~/</c>。</summary>
    public static string? ConfigDirectory(string mpvExecutablePath)
    {
        string? configPath = CandidateConfigPaths(mpvExecutablePath).FirstOrDefault(File.Exists);
        return configPath is null ? null : Path.GetDirectoryName(configPath);
    }

    /// <summary>
    /// 解析 mpv.conf：忽略注释与空行，遇到第一个节标题就停（节里的设置是 profile，只有被激活时才生效，
    /// 我们不做 profile 推断，只取顶层默认值），去掉值两侧引号。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                break;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim().Trim('"');

            if (!ScreenshotKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            settings[key] = value;
        }

        return settings;
    }

    private static IEnumerable<string> CandidateConfigPaths(string mpvExecutablePath)
    {
        string executableDirectory = Path.GetDirectoryName(Path.GetFullPath(mpvExecutablePath))!;
        yield return Path.Combine(executableDirectory, PortableConfigFolderName, ConfigFileName);
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mpv", ConfigFileName);
    }
}
