using System.IO;
using System.Text;

namespace TrophyShots.Diagnostics;

/// <summary>
/// M0 阶段的证据日志：每条都落盘并立即刷新，便于事后核对时序。
/// 后续里程碑换成结构化日志时保留同样的字段：时间、动作、结果。
/// </summary>
public sealed class FileLogger : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter? _writer;

    public FileLogger(string? path)
    {
        if (path is null)
        {
            return;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        Write("=== 会话开始 ===");
    }

    public void Info(string message) => Write(message);

    private void Write(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (_lock)
        {
            _writer?.WriteLine(line);
        }

        System.Diagnostics.Debug.WriteLine(line);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Write("=== 会话结束 ===");
            _writer?.Dispose();
        }
    }
}
