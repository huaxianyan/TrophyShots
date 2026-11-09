using Avalonia;
using TrophyShots.App.Services;

namespace TrophyShots.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        StartupDiagnostics.Record($"启动 pid={Environment.ProcessId} 参数：{string.Join(' ', args)}");
        AppOptions options = AppOptions.Parse(args);
        App.Options = options;
        StartupDiagnostics.Record($"选项：mpv={options.MpvPath} ffprobe={options.FfprobePath} dir={options.Directory} selftest={options.SelfTest}");

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            StartupDiagnostics.Record("UI 循环结束");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.RecordException("启动", exception);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
