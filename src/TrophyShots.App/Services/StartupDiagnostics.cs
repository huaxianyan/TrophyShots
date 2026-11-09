using System.IO;

namespace TrophyShots.App.Services;

/// <summary>
/// 启动期诊断：界面还没出来时也能留下痕迹，避免"双击没反应"这种无从下手的状态。
/// 固定写在临时目录，与 --log 的证据日志分开。
/// </summary>
public static class StartupDiagnostics
{
    private static readonly object Sync = new();

    public static string LogPath { get; } = Path.Combine(Path.GetTempPath(), "TrophyShots-startup.log");

    public static void Record(string message)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // 诊断失败不能影响启动。
        }
    }

    public static void RecordException(string stage, Exception exception) =>
        Record($"{stage} 失败：{exception.GetType().Name}: {exception.Message}");
}
