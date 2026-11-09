using System.IO;

namespace TrophyShots.App;

/// <summary>M1 的命令行接口，只服务开发与自检；用户正常使用走界面。</summary>
public sealed class AppOptions
{
    public string MpvPath { get; private init; } = ResolveBundled("mpv", "mpv.exe");
    public string FfprobePath { get; private init; } = ResolveBundled("ffprobe", "ffprobe.exe");
    public string? Directory { get; private init; }
    public string? Video { get; private init; }
    public string? LogPath { get; private init; }
    public string? SelfTest { get; private init; }
    public string? EvidenceDirectory { get; private init; }

    /// <summary>自检时把窗口挪到屏幕外，不遮挡、不抢焦点。</summary>
    public bool Offscreen { get; private init; }

    /// <summary>
    /// 渲染后端：<c>software</c>（默认）或 <c>gpu</c>。
    /// 本机装了 RivaTuner Statistics Server，它的 hook 会把走 D3D 的进程弄崩（gdi32full BEX64），
    /// 所以默认走软件渲染；视频本身由 mpv 在自己的窗口里画，不经过这条路径。
    /// </summary>
    public string RenderMode { get; private init; } = "software";

    /// <summary>选目录用哪套对话框：<c>auto</c>（默认）、<c>system</c>、<c>builtin</c>。</summary>
    public string Picker { get; private init; } = "auto";

    /// <summary>开发/自检用的截图规则覆盖；正常使用从 mpv 配置读。</summary>
    public string? ScreenshotDirectory { get; private init; }

    public string? ScreenshotTemplate { get; private init; }

    public static AppOptions Parse(string[] args)
    {
        string mpvPath = ResolveBundled("mpv", "mpv.exe");
        string ffprobePath = ResolveBundled("ffprobe", "ffprobe.exe");
        string? directory = null;
        string? video = null;
        string? logPath = null;
        string? selfTest = null;
        string? evidence = null;
        bool offscreen = false;
        string renderMode = "software";
        string picker = "auto";
        string? screenshotDirectory = null;
        string? screenshotTemplate = null;

        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : string.Empty;

            switch (args[i])
            {
                case "--mpv":
                    mpvPath = Next();
                    break;
                case "--ffprobe":
                    ffprobePath = Next();
                    break;
                case "--directory":
                    directory = Next();
                    break;
                case "--video":
                    video = Next();
                    break;
                case "--log":
                    logPath = Next();
                    break;
                case "--selftest":
                    selfTest = Next();
                    break;
                case "--evidence":
                    evidence = Next();
                    break;
                case "--offscreen":
                    offscreen = true;
                    break;
                case "--render":
                    renderMode = Next();
                    break;
                case "--picker":
                    picker = Next();
                    break;
                case "--screenshot-dir":
                    // 相对路径按当前工作目录补全，避免被当成"相对录像目录"。
                    screenshotDirectory = Path.GetFullPath(Next());
                    break;
                case "--screenshot-template":
                    screenshotTemplate = Next();
                    break;
            }
        }

        return new AppOptions
        {
            MpvPath = mpvPath,
            FfprobePath = ffprobePath,
            Directory = directory,
            Video = video,
            LogPath = logPath,
            SelfTest = selfTest,
            EvidenceDirectory = evidence,
            Offscreen = offscreen,
            RenderMode = renderMode,
            Picker = picker,
            ScreenshotDirectory = screenshotDirectory,
            ScreenshotTemplate = screenshotTemplate,
        };
    }

    /// <summary>优先用随包分发的可执行文件（M3 会自带 mpv 与 ffprobe），否则退回 PATH。</summary>
    private static string ResolveBundled(string folder, string executable)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, folder, executable);
        return File.Exists(bundled) ? bundled : Path.GetFileNameWithoutExtension(executable);
    }
}
