using System.Diagnostics;
using System.IO;
using System.Text;
using TrophyShots.Interop;

namespace TrophyShots.Mpv;

/// <summary>
/// 以子进程方式拉起 mpv 并托管它的生命周期。
/// 退出路径：IPC <c>quit</c> → 等待 → 必要时 <c>Kill</c>；另外用 Job object 兜住强杀主进程的场景。
/// </summary>
public sealed class MpvProcess : IDisposable
{
    private readonly Process _process;
    private IntPtr _job;
    private bool _disposed;

    public int ProcessId => _process.Id;
    public string PipeName { get; }
    public string PipePath => @"\\.\pipe\" + PipeName;

    private MpvProcess(Process process, IntPtr job, string pipeName)
    {
        _process = process;
        _job = job;
        PipeName = pipeName;
    }

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

    public static MpvProcess Start(string mpvExe, long wid, IReadOnlyList<string> extraArguments)
    {
        if (wid == 0)
        {
            throw new ArgumentException("嵌入窗口句柄不能为 0", nameof(wid));
        }

        string pipeName = "trophyshots-" + Guid.NewGuid().ToString("N");
        string pipePath = @"\\.\pipe\" + pipeName;

        var psi = new ProcessStartInfo(mpvExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(mpvExe)) ?? Environment.CurrentDirectory,
        };

        // 全程用参数数组，不拼命令行字符串。
        psi.ArgumentList.Add("--no-config");
        psi.ArgumentList.Add("--wid=" + wid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--input-ipc-server=" + pipePath);
        psi.ArgumentList.Add("--input-default-bindings=no");
        psi.ArgumentList.Add("--input-vo-keyboard=no");
        psi.ArgumentList.Add("--no-osc");
        psi.ArgumentList.Add("--no-osd-bar");
        psi.ArgumentList.Add("--osd-level=0");
        psi.ArgumentList.Add("--pause");
        psi.ArgumentList.Add("--keep-open=yes");
        psi.ArgumentList.Add("--idle=yes");
        psi.ArgumentList.Add("--hwdec=no");

        foreach (string arg in extraArguments)
        {
            psi.ArgumentList.Add(arg);
        }

        Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("mpv 启动失败：Process.Start 返回空");

        IntPtr job = CreateKillOnCloseJob();
        if (job != IntPtr.Zero && !NativeMethods.AssignProcessToJobObject(job, process.Handle))
        {
            NativeMethods.CloseHandle(job);
            job = IntPtr.Zero;
        }

        return new MpvProcess(process, job, pipeName);
    }

    /// <summary>轮询等待 mpv 退出，兼容"已经被 Kill"的情况。</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return HasExited;
        }
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
            // 已经在退出过程中。
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 没有权限或进程已消失，交给 WaitForExit 判定。
        }
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("pid=").Append(ProcessId);
        sb.Append(" pipe=").Append(PipePath);
        return sb.ToString();
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

        bool ok = NativeMethods.SetInformationJobObject(
            job,
            NativeMethods.JobObjectExtendedLimitInformation,
            ref info,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());

        if (!ok)
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
