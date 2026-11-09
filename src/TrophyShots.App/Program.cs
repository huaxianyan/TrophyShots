using Avalonia;
using TrophyShots.App.Services;

namespace TrophyShots.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        StartupDiagnostics.Record($"启动 pid={Environment.ProcessId} 参数：{string.Join(' ', args)}");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupDiagnostics.RecordException("AppDomain 未处理异常", e.ExceptionObject as Exception ?? new Exception("未知异常"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            StartupDiagnostics.RecordException("后台任务未观察异常", e.Exception);
            e.SetObserved();
        };

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
            .With(new Avalonia.Win32PlatformOptions
            {
                RenderingMode = ResolveRenderingMode(),
            })
            .WithInterFont()
            .LogToTrace();

    private static IReadOnlyList<Avalonia.Win32RenderingMode> ResolveRenderingMode()
    {
        bool gpu = string.Equals(App.Options.RenderMode, "gpu", StringComparison.OrdinalIgnoreCase);
        StartupDiagnostics.Record($"渲染后端：{(gpu ? "GPU（D3D/ANGLE）" : "软件渲染")}");
        return gpu
            ? [Avalonia.Win32RenderingMode.AngleEgl, Avalonia.Win32RenderingMode.Software]
            : [Avalonia.Win32RenderingMode.Software];
    }
}
