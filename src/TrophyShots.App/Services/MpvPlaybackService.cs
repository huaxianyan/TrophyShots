using System.Text.Json;
using TrophyShots.Player;

namespace TrophyShots.App.Services;

/// <summary>
/// 播放器的持有者：拉起一次 mpv，之后所有指令转发给它，属性事件原样往上抛。
/// 界面只认这一层，不关心 IPC 与进程细节。
/// </summary>
public sealed class MpvPlaybackService : IAsyncDisposable
{
    private readonly AppOptions _options;
    private readonly AppLogger _log;
    private MpvPlayer? _player;

    public MpvPlaybackService(AppOptions options, AppLogger log)
    {
        _options = options;
        _log = log;
    }

    public event Action<string, JsonElement>? PropertyChanged;

    /// <summary>mpv 进程断开或 IPC 读循环异常结束。</summary>
    public event Action<string>? Faulted;

    public bool IsStarted => _player is not null;

    public int ProcessId => _player?.ProcessId ?? 0;

    public async Task StartAsync(
        IntPtr hostHandle,
        Core.ScreenshotNaming naming,
        CancellationToken cancellationToken)
    {
        if (_player is not null)
        {
            return;
        }

        MpvPlayer player = await MpvPlayer.StartAsync(new MpvStartOptions
        {
            ExecutablePath = _options.MpvPath,
            HostWindowHandle = hostHandle.ToInt64(),
            ScreenshotTemplate = naming.Template,
            ScreenshotFormat = naming.Format,
        }, cancellationToken).ConfigureAwait(false);

        player.PropertyChanged += (name, value) => PropertyChanged?.Invoke(name, value);
        player.LogReceived += message => _log.Info($"mpv: {message}");
        player.Faulted += reason =>
        {
            _log.Info($"mpv 连接中断：{reason}");
            Faulted?.Invoke(reason);
        };
        _player = player;
    }

    public Task LoadAsync(string videoPath, CancellationToken cancellationToken) =>
        RequirePlayer().LoadAsync(videoPath, cancellationToken);

    public Task SeekAsync(double seconds, CancellationToken cancellationToken) =>
        RequirePlayer().SeekAsync(seconds, cancellationToken);

    public Task TogglePauseAsync(CancellationToken cancellationToken) =>
        RequirePlayer().TogglePauseAsync(cancellationToken);

    public Task StepAsync(int frames, CancellationToken cancellationToken) =>
        RequirePlayer().StepAsync(frames, cancellationToken);

    public Task<string?> TakeScreenshotAsync(string directory, string? expectedPath, CancellationToken cancellationToken) =>
        RequirePlayer().TakeScreenshotAsync(directory, expectedPath, cancellationToken);

    public Task<double?> GetNumberAsync(string property, CancellationToken cancellationToken) =>
        RequirePlayer().GetNumberAsync(property, cancellationToken);

    public Task<string?> GetStringAsync(string property, CancellationToken cancellationToken) =>
        RequirePlayer().GetStringAsync(property, cancellationToken);

    public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        RequirePlayer().WaitForFirstFrameAsync(timeout, cancellationToken);

    private MpvPlayer RequirePlayer() =>
        _player ?? throw new InvalidOperationException("播放器还没启动");

    public async ValueTask DisposeAsync()
    {
        if (_player is null)
        {
            return;
        }

        await _player.DisposeAsync().ConfigureAwait(false);
        _player = null;
    }
}
