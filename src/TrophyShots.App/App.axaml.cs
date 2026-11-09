using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TrophyShots.App.Services;
using TrophyShots.App.ViewModels;
using TrophyShots.App.Views;

namespace TrophyShots.App;

public partial class App : Avalonia.Application
{
    /// <summary>命令行选项，由 <see cref="Program"/> 在启动 Avalonia 之前解析好。</summary>
    public static AppOptions Options { get; set; } = new();

    /// <summary>证据日志；没给 --log 时只写调试输出。</summary>
    public static AppLogger Log { get; private set; } = new(null);

    public override void Initialize()
    {
        StartupDiagnostics.Record("App.Initialize 开始");
        AvaloniaXamlLoader.Load(this);
        StartupDiagnostics.Record("App.Initialize 完成");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        StartupDiagnostics.Record("OnFrameworkInitializationCompleted");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppOptions options = Options;
            Log = new AppLogger(options.LogPath);
            AppLogger log = Log;
            var viewModel = new MainWindowViewModel(options, log);
            var window = new MainWindow { DataContext = viewModel };

            desktop.MainWindow = window;
            desktop.ShutdownRequested += async (_, _) => await viewModel.ShutdownAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
