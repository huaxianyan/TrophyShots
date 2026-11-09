using System.IO;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrophyShots.App.Services;
using TrophyShots.Core;
using TrophyShots.Player;

namespace TrophyShots.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly AppOptions _options;
    private readonly AppLogger _log;
    private readonly MpvPlaybackService _player;
    private CancellationTokenSource _operation = new();
    private string? _directory;
    private double? _frameRate;
    private readonly ScreenshotNaming _naming;
    private readonly string? _mpvConfigDirectory;

    public MainWindowViewModel(AppOptions options, AppLogger log)
    {
        _options = options;
        _log = log;
        _mpvConfigDirectory = MpvConfigReader.ConfigDirectory(options.MpvPath);
        ScreenshotNaming configured = MpvConfigReader.ReadScreenshotNaming(options.MpvPath) ?? ScreenshotNaming.Default;
        _naming = configured with
        {
            Directory = options.ScreenshotDirectory ?? configured.Directory,
            Template = options.ScreenshotTemplate ?? configured.Template,
        };
        _log.Info($"截图规则：{_naming.Describe()}（配置目录 {_mpvConfigDirectory ?? "未找到"}）");

        _player = new MpvPlaybackService(options, log);
        _player.PropertyChanged += OnPlayerPropertyChanged;
    }

    /// <summary>选目录的窗口对话框由视图注入，ViewModel 不直接碰 UI 平台。</summary>
    public Func<Task<string?>>? DirectoryPicker { get; set; }

    /// <summary>当前打开的目录（没打开过就是 null），给目录选择器当起始位置。</summary>
    public string? CurrentDirectory => _directory;

    public System.Collections.ObjectModel.ObservableCollection<VideoItemViewModel> Videos { get; } = [];

    [ObservableProperty] private string _directoryPath = "尚未选择录像目录";
    [ObservableProperty] private string _progressText = "已处理 0 / 0";
    [ObservableProperty] private string _statusMessage = "选择一个装着录像的目录开始。";
    [ObservableProperty] private string _positionText = "00:00.000";
    [ObservableProperty] private string _frameText = "帧 —";
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _isPaused = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private VideoItemViewModel? _selectedVideo;

    public bool HasSelection => SelectedVideo is not null;

    /// <summary>拖动时间轴期间不要用播放器上报的位置覆盖滑块。</summary>
    public bool IsScrubbing { get; set; }

    // ---------------- 生命周期 ----------------

    /// <summary>视频宿主窗口就绪后拉起 mpv；有 --directory 时顺带打开该目录。</summary>
    public async Task AttachVideoHostAsync(IntPtr hostHandle)
    {
        try
        {
            await _player.StartAsync(hostHandle, _naming, _operation.Token);
            _log.Info($"播放器就绪 pid={_player.ProcessId} host=0x{hostHandle:X}");
            StatusMessage = "播放器已就绪，选择一条录像开始。";

            if (_options.Video is { } standalone)
            {
                await OpenStandaloneAsync(standalone);
            }
            else if (_options.Directory is { } initial)
            {
                await OpenDirectoryAsync(initial);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"播放器启动失败：{ex.Message}";
            _log.Info($"启动失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task ShutdownAsync()
    {
        SaveState(SelectedVideo, PositionSeconds);
        await _player.DisposeAsync();
    }

    // ---------------- 目录与列表 ----------------

    [RelayCommand]
    private async Task ChooseDirectoryAsync()
    {
        if (DirectoryPicker is null)
        {
            return;
        }

        _log.Info("选择目录：打开文件夹对话框");
        try
        {
            string? directory = await DirectoryPicker();
            _log.Info($"选择目录：返回 {directory ?? "(取消)"}");
            if (!string.IsNullOrEmpty(directory))
            {
                await OpenDirectoryAsync(directory);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开文件夹对话框失败：{ex.Message}";
            _log.Info($"选择目录失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>只打开一个文件（给真实素材的深度定位自检用），不扫描目录。</summary>
    public Task OpenStandaloneAsync(string videoPath)
    {
        var item = new VideoItemViewModel(videoPath, IsCompleted(videoPath));
        Videos.Add(item);
        UpdateProgress();
        SelectedVideo = item;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (_directory is not null)
        {
            await OpenDirectoryAsync(_directory);
        }
    }

    public async Task OpenDirectoryAsync(string directory)
    {
        _directory = directory;
        DirectoryPath = directory;
        Videos.Clear();
        SelectedVideo = null;
        IsReady = false;

        IReadOnlyList<string> files;
        try
        {
            files = VideoLibrary.CollectVideoFiles(directory);
        }
        catch (DirectoryNotFoundException)
        {
            StatusMessage = $"目录不存在：{directory}";
            return;
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取目录失败：{ex.Message}";
            return;
        }

        foreach (string file in files)
        {
            Videos.Add(new VideoItemViewModel(file, IsCompleted(file)));
        }

        UpdateProgress();

        if (Videos.Count == 0)
        {
            StatusMessage = "这个目录里没有找到录像文件（支持 mp4 / mkv / mov / webm / m4v）。";
            return;
        }

        StatusMessage = $"找到 {Videos.Count} 条录像，正在读取时长…";
        await ProbeDurationsAsync();

        ProcessingState state = ProcessingState.Load(VideoLibrary.StatePathFor(directory));
        VideoItemViewModel? target =
            Videos.FirstOrDefault(v => v.FileName == state.LastFileName)
            ?? Videos.FirstOrDefault(v => !v.Completed)
            ?? Videos.FirstOrDefault();

        StatusMessage = $"共 {Videos.Count} 条录像，已处理 {Videos.Count(v => v.Completed)} 条。";
        if (target is not null)
        {
            SelectedVideo = target;
        }
    }

    private async Task ProbeDurationsAsync()
    {
        using var throttle = new SemaphoreSlim(4);
        CancellationToken cancellationToken = _operation.Token;

        await Task.WhenAll(Videos.Select(async item =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                MediaProbeResult result = await MediaProbe.ProbeAsync(_options.FfprobePath, item.FullPath, cancellationToken);
                await Dispatcher.UIThread.InvokeAsync(() => item.ApplyProbe(result));
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                throttle.Release();
            }
        }));
    }

    /// <summary>已完成 = 按当前规则算出来的那张图存在。</summary>
    private bool IsCompleted(string videoPath)
    {
        string? target = _naming.TryResolveTarget(videoPath, _mpvConfigDirectory);
        return target is not null && File.Exists(target);
    }

    private void UpdateProgress()
    {
        ProgressText = $"已处理 {Videos.Count(v => v.Completed)} / {Videos.Count}";
    }

    // ---------------- 打开与定位 ----------------

    partial void OnSelectedVideoChanged(VideoItemViewModel? value)
    {
        if (value is not null)
        {
            _ = OpenVideoAsync(value);
        }
    }

    private async Task OpenVideoAsync(VideoItemViewModel item)
    {
        CancellationToken cancellationToken = BeginOperation();
        try
        {
            IsReady = false;
            FrameText = "帧 —";
            PositionText = "加载中…";
            DurationSeconds = Math.Max((item.Duration?.TotalSeconds) ?? 1, 0.1);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            await _player.LoadAsync(item.FullPath, cancellationToken);
            bool ready = await _player.WaitForFirstFrameAsync(TimeSpan.FromSeconds(20), cancellationToken);
            watch.Stop();

            if (!ready)
            {
                item.Broken = true;
                StatusMessage = $"「{item.FileName}」20 秒内没有解出画面，文件可能损坏，已跳过。";
                _log.Info($"首帧超时 {item.FullPath}");
                return;
            }

            _frameRate = await _player.GetNumberAsync("container-fps", cancellationToken);
            IsReady = true;
            StatusMessage = $"已打开 {item.FileName}";
            _log.Info($"打开 {item.FileName} 首帧 {watch.ElapsedMilliseconds}ms fps={_frameRate:0.###}");
            SaveState(item, 0);
            await RefreshPositionAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开「{item.FileName}」失败：{ex.Message}";
            _log.Info($"打开失败 {item.FileName} {ex.GetType().Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private Task StepBackwardAsync() => StepAsync(-1);

    [RelayCommand]
    private Task StepBackwardTenAsync() => StepAsync(-10);

    [RelayCommand]
    private Task StepForwardAsync() => StepAsync(1);

    [RelayCommand]
    private Task StepForwardTenAsync() => StepAsync(10);

    [RelayCommand]
    private Task SecondBackwardAsync() => SeekRelativeAsync(-1);

    [RelayCommand]
    private Task SecondForwardAsync() => SeekRelativeAsync(1);

    [RelayCommand]
    private async Task TogglePlayAsync()
    {
        if (!IsReady)
        {
            return;
        }

        try
        {
            await _player.TogglePauseAsync(_operation.Token);
            await RefreshPositionAsync(_operation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"播放控制失败：{ex.Message}";
        }
    }

    public async Task StepAsync(int frames)
    {
        if (!IsReady)
        {
            return;
        }

        CancellationToken cancellationToken = _operation.Token;
        try
        {
            await _player.StepAsync(frames, cancellationToken);
            await RefreshPositionAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"逐帧失败：{ex.Message}";
        }
    }

    private async Task SeekRelativeAsync(double seconds)
    {
        if (!IsReady)
        {
            return;
        }

        double current = PositionSeconds;
        await SeekToAsync(Math.Clamp(current + seconds, 0, DurationSeconds));
    }

    public async Task SeekToAsync(double seconds)
    {
        if (!IsReady)
        {
            return;
        }

        CancellationToken cancellationToken = _operation.Token;
        try
        {
            await _player.SeekAsync(Math.Clamp(seconds, 0, DurationSeconds), cancellationToken);
            await RefreshPositionAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"定位失败：{ex.Message}";
        }
    }

    /// <summary>当前的截图规则与录像规则，自检用来核对落盘位置。</summary>
    public string? ScreenshotTargetFor(string videoPath) => _naming.TryResolveTarget(videoPath, _mpvConfigDirectory);

    /// <summary>自检用：直接读 mpv 的估算帧号。</summary>
    public async Task<double?> ReadFrameAsync(CancellationToken cancellationToken) =>
        IsReady ? await _player.GetNumberAsync("estimated-frame-number", cancellationToken) : null;

    // ---------------- 截图 ----------------

    [RelayCommand]
    private Task CaptureAsync() => CaptureCoreAsync(advance: true);

    [RelayCommand]
    private Task RecaptureAsync() => CaptureCoreAsync(advance: false);

    private async Task CaptureCoreAsync(bool advance)
    {
        if (!IsReady || SelectedVideo is null)
        {
            return;
        }

        VideoItemViewModel item = SelectedVideo;
        string directory = _naming.ResolveDirectoryFor(item.FullPath, _mpvConfigDirectory);
        string? expected = _naming.TryResolveTarget(item.FullPath, _mpvConfigDirectory);
        CancellationToken cancellationToken = _operation.Token;

        try
        {
            string? saved = await _player.TakeScreenshotAsync(directory, expected, cancellationToken);

            if (saved is null)
            {
                StatusMessage = $"截图没有写出来，检查「{directory}」是否可写。";
                _log.Info($"截图未落盘 directory={directory} expected={expected ?? "(模板算不出)"}");
                return;
            }

            item.Completed = true;
            UpdateProgress();
            SaveState(item, PositionSeconds);
            _log.Info($"截图 {saved} bytes={new FileInfo(saved).Length} frame={FrameText}");

            StatusMessage = advance
                ? $"已保存 {Path.GetFileName(saved)}，进入下一条。"
                : $"已覆盖 {Path.GetFileName(saved)}。";

            if (advance)
            {
                await MoveAsync(1);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"截图失败：{ex.Message}";
            _log.Info($"截图失败 directory={directory} {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------- 切换录像 ----------------

    [RelayCommand]
    private Task NextAsync() => MoveAsync(1);

    [RelayCommand]
    private Task PreviousAsync() => MoveAsync(-1);

    private async Task MoveAsync(int offset)
    {
        if (SelectedVideo is null || Videos.Count == 0)
        {
            return;
        }

        int index = Videos.IndexOf(SelectedVideo);
        int target = index + offset;
        if (target < 0)
        {
            StatusMessage = "已经是第一条。";
            return;
        }

        if (target >= Videos.Count)
        {
            StatusMessage = "已经是最后一条。";
            await Task.CompletedTask;
            return;
        }

        SelectedVideo = Videos[target];
    }

    // ---------------- 播放器事件 ----------------

    private void OnPlayerPropertyChanged(string name, JsonElement value)
    {
        // mpv 的事件在读线程上到达，界面状态统一回 UI 线程改。
        Dispatcher.UIThread.Post(() => ApplyProperty(name, value));
    }

    private void ApplyProperty(string name, JsonElement value)
    {
        switch (name)
        {
            case "time-pos" when value.ValueKind == JsonValueKind.Number:
                ApplyPosition(value.GetDouble());
                break;
            case "estimated-frame-number" when value.ValueKind == JsonValueKind.Number:
                ApplyFrame(value.GetDouble());
                break;
            case "pause" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                IsPaused = value.GetBoolean();
                break;
            case "duration" when value.ValueKind == JsonValueKind.Number && value.GetDouble() > 0:
                DurationSeconds = value.GetDouble();
                break;
            case "container-fps" when value.ValueKind == JsonValueKind.Number:
                _frameRate = value.GetDouble();
                break;
        }
    }

    private void ApplyPosition(double seconds)
    {
        if (!IsScrubbing)
        {
            PositionSeconds = seconds;
        }

        PositionText = FormatPosition(seconds);
    }

    private void ApplyFrame(double frame)
    {
        double total = (_frameRate ?? 0) * DurationSeconds;
        FrameText = total >= 1 ? $"帧 {frame:0} / {total:0}" : $"帧 {frame:0}";
    }

    public static string FormatPosition(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(seconds, 0));
        return $"{(int)span.TotalMinutes:00}:{span.Seconds:00}.{span.Milliseconds:000}";
    }

    private async Task RefreshPositionAsync(CancellationToken cancellationToken)
    {
        double? position = await _player.GetNumberAsync("time-pos", cancellationToken);
        double? frame = await _player.GetNumberAsync("estimated-frame-number", cancellationToken);
        double? duration = await _player.GetNumberAsync("duration", cancellationToken);

        if (duration is > 0)
        {
            DurationSeconds = duration.Value;
        }

        if (position.HasValue)
        {
            ApplyPosition(position.Value);
        }

        if (frame.HasValue)
        {
            ApplyFrame(frame.Value);
        }
    }

    // ---------------- 状态文件 ----------------

    private void SaveState(VideoItemViewModel? item, double position)
    {
        if (_directory is null)
        {
            return;
        }

        try
        {
            new ProcessingState
            {
                LastFileName = item?.FileName,
                LastPositionSeconds = position,
            }.Save(VideoLibrary.StatePathFor(_directory));
        }
        catch (Exception ex)
        {
            _log.Info($"写状态失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private CancellationToken BeginOperation()
    {
        CancellationTokenSource previous = _operation;
        _operation = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        return _operation.Token;
    }
}
