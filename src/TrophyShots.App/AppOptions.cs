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
        };
    }

    /// <summary>优先用随包分发的可执行文件（M3 会自带 mpv 与 ffprobe），否则退回 PATH。</summary>
    private static string ResolveBundled(string folder, string executable)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, folder, executable);
        return File.Exists(bundled) ? bundled : Path.GetFileNameWithoutExtension(executable);
    }
}
