using System.IO;
using System.Text;

namespace TrophyShots.App.Services;

/// <summary>开发期证据日志：每条立即刷盘，便于事后核对时序。用户正常使用时只在指定 --log 时才写。</summary>
public sealed class AppLogger
{
    private readonly object _lock = new();
    private readonly StreamWriter? _writer;

    public AppLogger(string? path)
    {
        if (path is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(Path.GetFullPath(path), append: true, new UTF8Encoding(false)) { AutoFlush = true };
        Info("=== 会话开始 ===");
    }

    public void Info(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (_lock)
        {
            _writer?.WriteLine(line);
        }

        System.Diagnostics.Debug.WriteLine(line);
    }
}
