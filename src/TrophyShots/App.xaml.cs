using System.Windows;
using TrophyShots.Diagnostics;

namespace TrophyShots;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = CommandLineOptions.Parse(e.Args);
        var logger = new FileLogger(options.LogPath);

        var window = new MainWindow(options, logger);
        MainWindow = window;
        window.Show();
    }
}

/// <summary>M0 只接受少量调试参数，正式命令行接口留到 M1 再定。</summary>
public sealed class CommandLineOptions
{
    public string? VideoPath { get; private set; }
    public string? LogPath { get; private set; }
    public string? MpvPath { get; private set; } = "mpv";
    public string? EvidenceDirectory { get; private set; }

    /// <summary>非空时启动后自动跑自检并在结束时关闭窗口。</summary>
    public string? SelfTest { get; private set; }

    /// <summary>把自检窗口放到屏幕外，不遮挡、不打断正常使用。</summary>
    public bool Offscreen { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string value = i + 1 < args.Length ? args[i + 1] : "";
            switch (args[i])
            {
                case "--video":
                    options.VideoPath = value;
                    i++;
                    break;
                case "--log":
                    options.LogPath = value;
                    i++;
                    break;
                case "--mpv":
                    options.MpvPath = value;
                    i++;
                    break;
                case "--evidence":
                    options.EvidenceDirectory = value;
                    i++;
                    break;
                case "--selftest":
                    options.SelfTest = value;
                    i++;
                    break;
                case "--offscreen":
                    options.Offscreen = true;
                    break;
            }
        }

        return options;
    }
}
