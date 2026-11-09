using System.Diagnostics;

namespace TrophyShots.Player;

public sealed record MpvStartOptions
{
    /// <summary>mpv 可执行文件路径。要给真实 exe，不要给 Scoop shim（会多起一个子进程）。</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>嵌入目标窗口句柄，作为 <c>--wid</c> 传入。</summary>
    public required long HostWindowHandle { get; init; }

    /// <summary>截图文件名规则，取用户 mpv 配置里的值；为空时用 mpv 自己的默认。</summary>
    public string? ScreenshotTemplate { get; init; }

    public string? ScreenshotFormat { get; init; }

    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}

/// <summary>
/// 以子进程方式拉起 mpv 并托管生命周期。
/// 退出路径：IPC <c>quit</c> → 等待 → 必要时 Kill；另外用 Job object 兜住强杀主进程的场景。
/// </summary>
public sealed class MpvProcess : IDisposable
{
    private readonly Process _process;
    private readonly TimeSpan _exitTimeout;
    private IntPtr _job;
    private bool _disposed;

    private MpvProcess(Process process, IntPtr job, string pipePath)
    {
        _process = process;
        _job = job;
        PipePath = pipePath;
        _exitTimeout = TimeSpan.FromSeconds(3);
    }

    public string PipePath { get; }

    /// <summary>mpv 进程退出（不论原因）。</summary>
    public event Action? Exited;

    public int ProcessId => _process.Id;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public static MpvProcess Start(MpvStartOptions options)
    {
        if (options.HostWindowHandle == 0)
        {
            throw new ArgumentException("嵌入窗口句柄不能为 0", nameof(options));
        }

        string pipePath = @"\\.\pipe\trophyshots-" + Guid.NewGuid().ToString("N");
        var startInfo = new ProcessStartInfo(options.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ExecutablePath))!,
        };

        // 全程用参数数组，绝不拼命令行字符串。
        foreach (string argument in BuildArguments(options, pipePath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("mpv 启动失败：Process.Start 返回空");

        IntPtr job = CreateKillOnCloseJob();
        if (job != IntPtr.Zero && !NativeMethods.AssignProcessToJobObject(job, process.Handle))
        {
            NativeMethods.CloseHandle(job);
            job = IntPtr.Zero;
        }

        var instance = new MpvProcess(process, job, pipePath);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => instance.Exited?.Invoke();
        return instance;
    }

    private static IEnumerable<string> BuildArguments(MpvStartOptions options, string pipePath)
    {
        // --no-config：不受用户 mpv.conf 影响，行为可复现。
        yield return "--no-config";
        yield return "--wid=" + options.HostWindowHandle.ToString(System.Globalization.CultureInfo.InvariantCulture);
        yield return "--input-ipc-server=" + pipePath;
        yield return "--input-default-bindings=no";   // 键位由 UI 决定
        yield return "--input-vo-keyboard=no";
        yield return "--no-osc";
        yield return "--no-osd-bar";
        yield return "--osd-level=0";
        yield return "--pause";                        // 打开即暂停，显示首帧
        yield return "--keep-open=yes";
        yield return "--idle=yes";
        yield return "--hwdec=no";                     // M0 实测软件解码单步 3ms；硬解留到后续评估
        yield return "--hr-seek=yes";

        if (!string.IsNullOrEmpty(options.ScreenshotTemplate))
        {
            yield return "--screenshot-template=" + options.ScreenshotTemplate;
        }

        if (!string.IsNullOrEmpty(options.ScreenshotFormat))
        {
            yield return "--screenshot-format=" + options.ScreenshotFormat;
        }

        foreach (string argument in options.ExtraArguments)
        {
            yield return argument;
        }
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return HasExited;
        }
    }

    /// <summary>先礼后兵：等优雅退出，超时再强杀。</summary>
    public async Task<bool> EnsureExitedAsync()
    {
        if (await WaitForExitAsync(_exitTimeout).ConfigureAwait(false))
        {
            return true;
        }

        Kill();
        return await WaitForExitAsync(_exitTimeout).ConfigureAwait(false);
    }

    public void Kill()
    {
        if (HasExited)
        {
            return;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 已在退出过程中。
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 权限不足或进程已消失，交给 EnsureExitedAsync 判定。
        }
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        IntPtr job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        bool configured = NativeMethods.SetInformationJobObject(
            job,
            NativeMethods.JobObjectExtendedLimitInformation,
            ref info,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());

        if (!configured)
        {
            NativeMethods.CloseHandle(job);
            return IntPtr.Zero;
        }

        return job;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_job != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_job);
            _job = IntPtr.Zero;
        }

        _process.Dispose();
    }
}
