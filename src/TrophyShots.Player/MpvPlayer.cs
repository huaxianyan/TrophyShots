using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace TrophyShots.Player;

/// <summary>
/// 面向界面的播放器封装：拉起 mpv、订阅属性、逐帧、定位、截图、退出。
/// 帧语义全部交给 mpv 执行，这里只负责顺序与确认（连发回退会被 mpv 丢掉，见 StepOnceAsync）。
/// </summary>
public sealed class MpvPlayer : IAsyncDisposable
{
    /// <summary>界面需要的属性，启动时统一订阅。</summary>
    private static readonly string[] ObservedProperties =
    [
        "filename", "width", "height", "duration", "container-fps",
        "osd-dimensions", "time-pos", "estimated-frame-number", "pause", "video-format",
    ];

    private readonly MpvProcess _process;
    private readonly MpvIpc _ipc;
    private bool _disposing;

    private MpvPlayer(MpvProcess process, MpvIpc ipc)
    {
        _process = process;
        _ipc = ipc;
        _ipc.PropertyChanged += (name, value) => PropertyChanged?.Invoke(name, value);
        _ipc.Faulted += reason => Faulted?.Invoke(reason);
    }

    /// <summary>mpv 的属性变化事件（在 IPC 读线程上触发，调用方自己切线程）。</summary>
    public event Action<string, JsonElement>? PropertyChanged;

    public event Action<string>? Faulted;

    public int ProcessId => _process.ProcessId;

    public static async Task<MpvPlayer> StartAsync(MpvStartOptions options, CancellationToken cancellationToken)
    {
        MpvProcess process = MpvProcess.Start(options);
        try
        {
            MpvIpc ipc = await MpvIpc.ConnectAsync(process.PipePath, TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);

            foreach (string property in ObservedProperties)
            {
                await ipc.ObservePropertyAsync(property, cancellationToken).ConfigureAwait(false);
            }

            var player = new MpvPlayer(process, ipc);
            process.Exited += () =>
            {
                if (!player._disposing)
                {
                    player.Faulted?.Invoke("mpv 进程已退出");
                }
            };

            return player;
        }
        catch
        {
            process.Kill();
            process.Dispose();
            throw;
        }
    }

    public Task<JsonElement> CommandAsync(CancellationToken cancellationToken, params object?[] command) =>
        _ipc.CommandAsync(cancellationToken, command);

    public Task LoadAsync(string videoPath, CancellationToken cancellationToken) =>
        _ipc.CommandAsync(cancellationToken, "loadfile", Path.GetFullPath(videoPath), "replace");

    public Task SeekAsync(double seconds, CancellationToken cancellationToken) =>
        _ipc.CommandAsync(cancellationToken, "seek", seconds, "absolute+exact");

    public Task SetPausedAsync(bool paused, CancellationToken cancellationToken) =>
        _ipc.CommandAsync(cancellationToken, "set_property", "pause", paused);

    public Task TogglePauseAsync(CancellationToken cancellationToken) =>
        _ipc.CommandAsync(cancellationToken, "cycle", "pause");

    /// <summary>
    /// 让 mpv 按它自己的 <c>screenshot-directory</c> / <c>screenshot-template</c> 截一张原分辨率图，
    /// 返回实际落盘的文件。文件名由 mpv 决定，所以先看 <paramref name="expectedPath"/>，
    /// 再退回"目录里最新的那次写入"。
    /// </summary>
    public async Task<string?> TakeScreenshotAsync(
        string screenshotDirectory,
        string? expectedPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(screenshotDirectory);
        DateTime startedUtc = DateTime.UtcNow.AddSeconds(-2);

        // 目录按当前录像算（默认规则是"录像同目录的 screenshots/"），所以每次截图前重设一次。
        await _ipc.CommandAsync(cancellationToken, "set_property", "screenshot-directory", screenshotDirectory)
            .ConfigureAwait(false);
        await _ipc.CommandAsync(cancellationToken, "screenshot", "video").ConfigureAwait(false);

        if (expectedPath is not null
            && File.Exists(expectedPath)
            && File.GetLastWriteTimeUtc(expectedPath) >= startedUtc)
        {
            return expectedPath;
        }

        for (int attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);

            FileInfo? newest = new DirectoryInfo(screenshotDirectory)
                .EnumerateFiles()
                .Where(file => file.LastWriteTimeUtc >= startedUtc)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newest is not null)
            {
                return newest.FullName;
            }
        }

        return null;
    }

    public async Task<double?> GetNumberAsync(string property, CancellationToken cancellationToken)
    {
        JsonElement? value = await _ipc.TryGetPropertyAsync(property, cancellationToken).ConfigureAwait(false);
        return value is { ValueKind: JsonValueKind.Number } element ? element.GetDouble() : null;
    }

    public async Task<string?> GetStringAsync(string property, CancellationToken cancellationToken)
    {
        JsonElement? value = await _ipc.TryGetPropertyAsync(property, cancellationToken).ConfigureAwait(false);
        return value is { ValueKind: JsonValueKind.String } element ? element.GetString() : null;
    }

    /// <summary>首帧之前 width / estimated-frame-number 都是 unavailable，必须等它出现才能读数。</summary>
    public async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double? frame = await GetNumberAsync("estimated-frame-number", cancellationToken).ConfigureAwait(false);
            double? width = await GetNumberAsync("width", cancellationToken).ConfigureAwait(false);
            if (frame.HasValue && width.HasValue)
            {
                return true;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// 前进/后退若干帧。每一步都要等 time-pos 真的变了再发下一条：
    /// mpv 会丢掉 seek 期间到达的 frame-back-step 请求（M0 实测连发 10 条只退 1 帧）。
    /// </summary>
    public async Task StepAsync(int frames, CancellationToken cancellationToken)
    {
        bool forward = frames > 0;
        for (int i = 0; i < Math.Abs(frames); i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StepOnceAsync(forward, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StepOnceAsync(bool forward, CancellationToken cancellationToken)
    {
        double? before = await GetNumberAsync("time-pos", cancellationToken).ConfigureAwait(false);
        await CommandAsync(cancellationToken, forward ? "frame-step" : "frame-back-step").ConfigureAwait(false);

        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 500)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            double? now = await GetNumberAsync("time-pos", cancellationToken).ConfigureAwait(false);
            if (now.HasValue && now != before)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;

        try
        {
            await _ipc.CommandAsync(CancellationToken.None, "quit").ConfigureAwait(false);
        }
        catch (Exception)
        {
            // mpv 可能已经自己退出，下面按进程状态兜底。
        }

        await _process.EnsureExitedAsync().ConfigureAwait(false);
        await _ipc.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
    }
}
