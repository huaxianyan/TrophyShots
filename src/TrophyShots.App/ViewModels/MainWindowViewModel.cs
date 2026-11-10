using System.IO;
using System.Text.Json;
using Avalonia.Media.Imaging;
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
    private readonly ThumbnailService _thumbnails;
    private readonly TrophyDetector _trophyDetector;
    private readonly TrophyStore _trophyStore;
    private readonly Dictionary<string, int> _trophyCursor = new();
    private CancellationTokenSource? _preprocess;
    private CancellationTokenSource _operation = new();
    private CancellationTokenSource _scan = new();
    private string? _directory;
    private double? _frameRate;
    private readonly TaskCompletionSource<bool> _playerStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ScreenshotNaming _naming;
    private readonly string? _mpvConfigDirectory;
    private IntPtr _hostHandle;
    private bool _shuttingDown;
    private ProcessingState _state = new();

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
        _thumbnails = new ThumbnailService(options, log);
        _trophyDetector = new TrophyDetector(options.FfmpegPath, options.FfprobePath);
        _trophyStore = new TrophyStore();
        _player.PropertyChanged += OnPlayerPropertyChanged;
        _player.Faulted += OnPlayerFaulted;
    }

    /// <summary>选目录的窗口对话框由视图注入，ViewModel 不直接碰 UI 平台。</summary>
    public Func<Task<string?>>? DirectoryPicker { get; set; }

    /// <summary>当前打开的目录（没打开过就是 null），给目录选择器当起始位置。</summary>
    public string? CurrentDirectory => _directory;

    /// <summary>后台预处理（识别跳奖杯片段）是否在跑，界面据此显示角落进度条。</summary>
    [ObservableProperty]
    private bool _isPreprocessing;

    [ObservableProperty]
    private double _preprocessProgress;

    [ObservableProperty]
    private string _preprocessText = "";

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

    /// <summary>非常帧率录像：逐帧改成按时间步进，界面要提示。</summary>
    [ObservableProperty] private bool _isTimeBasedStepping;

    /// <summary>全部处理完（或只剩出错条目）时抛给界面显示汇总。</summary>
    public event Action<LibrarySummary>? SummaryRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackTitle))]
    [NotifyPropertyChangedFor(nameof(PlaybackSubtitle))]
    private VideoItemViewModel? _selectedVideo;

    /// <summary>有录像列表时才把 mpv 的原生窗口放出来，避免开机就是一大块黑。</summary>
    public bool IsPlayerVisible => Videos.Count > 0;

    public string PlaybackTitle => SelectedVideo?.FileName ?? "还没有打开录像";

    public string PlaybackSubtitle => SelectedVideo is null
        ? "选目录 → 挑录像 → 逐帧微调 → 截图"
        : $"{(IsTimeBasedStepping ? "非常帧率：按时间步进" : "逐帧：← / →")}　·　{PositionText}　·　{FrameText}";

    /// <summary>拖动时间轴期间不要用播放器上报的位置覆盖滑块。</summary>
    public bool IsScrubbing { get; set; }

    // ---------------- 生命周期 ----------------

    /// <summary>
    /// 视频宿主窗口就绪后拉起 mpv。
    /// 宿主窗口可能被重建（Avalonia 的 NativeControlHost 在可见性/布局变化时会重建），
    /// 那时 mpv 还在往已经销毁的旧窗口里画——表现就是"有声音没画面"，必须换到新句柄上重接。
    /// </summary>
    public async Task AttachVideoHostAsync(IntPtr hostHandle)
    {
        bool reattaching = _player.IsStarted && _hostHandle != IntPtr.Zero && _hostHandle != hostHandle;
        _log.Info($"宿主窗口 0x{hostHandle:X}（上一个 0x{_hostHandle:X}，重接={reattaching}）");
        _hostHandle = hostHandle;

        if (reattaching)
        {
            await RestartPlayerAsync("播放窗口重建了，正在把画面接回来…");
            return;
        }

        try
        {
            await _player.StartAsync(hostHandle, _naming, _operation.Token);
            _log.Info($"播放器就绪 pid={_player.ProcessId} host=0x{hostHandle:X}");
            StatusMessage = "播放器已就绪，选择一条录像开始。";
            _playerStarted.TrySetResult(true);

            // 选录像可能早于播放器就绪（列表先出现），这里补开。
            if (SelectedVideo is { } pending && !IsReady)
            {
                await OpenVideoAtAsync(pending, null);
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
        _preprocess?.Cancel();

        _shuttingDown = true;
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
        OnPropertyChanged(nameof(IsPlayerVisible));
        SelectedVideo = item;
        StartTrophyPreprocessing();
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
        // 扫描与探测有自己的令牌：切换/打开某条录像会取消播放器操作，但不该打断探测。
        CancellationTokenSource previousScan = _scan;
        _scan = new CancellationTokenSource();
        previousScan.Cancel();
        previousScan.Dispose();

        _log.Info($"扫描目录 {directory}");
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
        OnPropertyChanged(nameof(IsPlayerVisible));

        if (Videos.Count == 0)
        {
            StatusMessage = "这个目录里没有找到录像文件（支持 mp4 / mkv / mov / webm / m4v）。";
            return;
        }

        StatusMessage = $"找到 {Videos.Count} 条录像，正在读取时长…";
        await ProbeDurationsAsync();
        _ = LoadThumbnailsAsync(Videos.ToArray());
        StartTrophyPreprocessing();

        _state = ProcessingState.Load(VideoLibrary.StatePathFor(directory));
        VideoItemViewModel? target =
            Videos.FirstOrDefault(v => v.FileName == _state.LastFileName)
            ?? Videos.FirstOrDefault(v => !v.Completed && !v.Broken)
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
        CancellationToken cancellationToken = _scan.Token;

        await Task.WhenAll(Videos.Select(async item =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                MediaProbeResult result = await MediaProbe.ProbeAsync(_options.FfprobePath, item.FullPath, cancellationToken);
                _log.Info($"探测 {item.FileName} duration={result.Duration?.TotalSeconds:0.###}s fps={result.FrameRate:0.###} vfr={result.IsVariableFrameRate} samples={result.SampleCount}");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    item.ApplyProbe(result);
                    // 探测可能晚于"打开"，选中项的步进方式要跟着补上。
                    if (ReferenceEquals(item, SelectedVideo))
                    {
                        IsTimeBasedStepping = item.IsVariableFrameRate;
                    }
                });
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

    /// <summary>缩略图后台生成，慢一点没关系，别卡住列表。</summary>
    private async Task LoadThumbnailsAsync(IReadOnlyList<VideoItemViewModel> items)
    {
        using var throttle = new SemaphoreSlim(3);
        CancellationToken cancellationToken = _scan.Token;

        await Task.WhenAll(items.Select(async item =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                Bitmap? bitmap = await _thumbnails.GetAsync(
                    item.FullPath, item.Duration ?? TimeSpan.Zero, cancellationToken);

                if (bitmap is not null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => item.Thumbnail = bitmap);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.Info($"缩略图异常 {item.FileName} {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                throttle.Release();
            }
        }));
    }

    /// <summary>扫描完目录就后台跑奖杯识别：先用缓存，缺的逐条检测并刷新进度。</summary>
    private void StartTrophyPreprocessing()
    {
        _preprocess?.Cancel();
        _preprocess?.Dispose();
        _preprocess = null;

        if (!TrophyDetector.IsSupported || Videos.Count == 0)
        {
            return;
        }

        var token = CancellationTokenSource.CreateLinkedTokenSource(_scan.Token);
        _preprocess = token;
        _ = PreprocessTrophiesAsync(Videos.ToArray(), token.Token);
    }

    private async Task PreprocessTrophiesAsync(IReadOnlyList<VideoItemViewModel> items, CancellationToken cancellationToken)
    {
        foreach (VideoItemViewModel item in items)
        {
            IReadOnlyList<TrophySegment>? cached = _trophyStore.TryGet(item.FullPath);
            if (cached is not null)
            {
                item.ApplyTrophies(cached);
            }
            else
            {
                item.MarkTrophyPending();
            }
        }

        VideoItemViewModel[] pending = items
            .Where(item => !item.Broken && _trophyStore.TryGet(item.FullPath) is null)
            .ToArray();

        if (pending.Length == 0)
        {
            return;
        }

        IsPreprocessing = true;
        PreprocessProgress = 0;
        int done = 0;

        try
        {
            foreach (VideoItemViewModel item in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                item.MarkTrophyRunning();
                PreprocessText = $"正在识别跳奖杯 {done + 1} / {pending.Length}：{item.FileName}";

                try
                {
                    IReadOnlyList<TrophySegment> segments = await Task.Run(
                        () => _trophyDetector.DetectAsync(
                            item.FullPath,
                            message => _log.Info($"奖杯诊断 {item.FileName}: {message}"),
                            cancellationToken), cancellationToken);
                    _trophyStore.Save(item.FullPath, segments);
                    item.ApplyTrophies(segments);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Info($"奖杯识别失败 {item.FileName} {ex.GetType().Name}: {ex.Message}");
                    item.MarkTrophyFailed();
                }

                done++;
                PreprocessProgress = (double)done / pending.Length;
                PreprocessText = $"正在识别跳奖杯 {done} / {pending.Length}：{item.FileName}";
            }

            PreprocessText = $"奖杯识别完成：{pending.Length} 条";
        }
        catch (OperationCanceledException)
        {
            PreprocessText = "";
        }
        finally
        {
            IsPreprocessing = false;
        }
    }

    /// <summary>在有奖杯的片段起点之间循环跳转（再次按就跳下一个）。</summary>
    [RelayCommand]
    private async Task JumpToTrophyAsync()
    {
        if (SelectedVideo is not { } item)
        {
            return;
        }

        if (item.Segments.Count == 0)
        {
            StatusMessage = item.TrophyText == "检测中…"
                ? "奖杯还在识别，稍等一下"
                : "这条录像没有识别到跳奖杯的片段";
            return;
        }

        // 默认落在最后一枚（截图要的就是最后跳的那个），再按往前翻。
        int cursor = _trophyCursor.TryGetValue(item.FullPath, out int previous)
            ? (previous - 1 + item.Segments.Count) % item.Segments.Count
            : item.Segments.Count - 1;
        _trophyCursor[item.FullPath] = cursor;

        TrophySegment segment = item.Segments[cursor];
        await SeekToAsync(segment.JumpSeconds);
        StatusMessage = $"奖杯 {cursor + 1} / {item.Segments.Count} · 跳到 {FormatPosition(segment.StartSeconds)}（再按一次往前翻）";
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
        OnPropertyChanged(nameof(PlaybackSubtitle));

        if (value is not null)
        {
            _ = OpenVideoAsync(value);
        }
    }

    partial void OnPositionTextChanged(string value) => OnPropertyChanged(nameof(PlaybackSubtitle));

    partial void OnFrameTextChanged(string value) => OnPropertyChanged(nameof(PlaybackSubtitle));

    partial void OnIsTimeBasedSteppingChanged(bool value) => OnPropertyChanged(nameof(PlaybackSubtitle));

    private Task OpenVideoAsync(VideoItemViewModel item) => OpenVideoAtAsync(item, null);

    /// <summary>打开录像。<paramref name="positionOverride"/> 有值时用它，否则按状态文件续做。</summary>
    private async Task OpenVideoAtAsync(VideoItemViewModel item, double? positionOverride)
    {
        CancellationToken cancellationToken = BeginOperation();
        try
        {
            await _playerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            IsReady = false;
            FrameText = "帧 —";
            PositionText = "加载中…";
            DurationSeconds = Math.Max((item.Duration?.TotalSeconds) ?? 1, 0.1);
            IsTimeBasedStepping = item.IsVariableFrameRate;

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
            _log.Info($"打开 {item.FileName} 首帧 {watch.ElapsedMilliseconds}ms fps={_frameRate:0.###} vfr={item.IsVariableFrameRate}");

            double resume = positionOverride ?? ResumePolicy.Resolve(_state, item.FileName, DurationSeconds);

            if (resume > ResumePolicy.MinimumPositionSeconds)
            {
                await _player.SeekAsync(resume, cancellationToken);
                await RefreshPositionAsync(cancellationToken);
                StatusMessage = $"已打开 {item.FileName}，从上次 {FormatPosition(resume)} 继续。";
                _log.Info($"续做 {item.FileName} position={resume:0.000}");
            }
            else
            {
                StatusMessage = item.IsVariableFrameRate
                    ? $"已打开 {item.FileName}（非常帧率：逐帧改为按时间步进）"
                    : $"已打开 {item.FileName}";
                await RefreshPositionAsync(cancellationToken);
            }

            SaveState(item, resume);
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
            if (IsTimeBasedStepping)
            {
                // 非常帧率下 mpv 的 frame-step 语义不稳定，按 1/fps 秒换算成时间步进。
                double secondsPerFrame = 1.0 / (_frameRate is > 0 ? _frameRate.Value : 60.0);
                await SeekToAsync(PositionSeconds + (frames * secondsPerFrame));
                return;
            }

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
            SaveState(SelectedVideo, PositionSeconds);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"定位失败：{ex.Message}";
        }
    }

    /// <summary>当前 mpv 进程号，自检用来模拟"播放器挂掉"。</summary>
    public int PlayerProcessId => _player.ProcessId;

    /// <summary>当前的截图规则与录像规则，自检用来核对落盘位置。</summary>
    public string? ScreenshotTargetFor(string videoPath) => _naming.TryResolveTarget(videoPath, _mpvConfigDirectory);

    /// <summary>自检用：读任意 mpv 数值属性（丢帧数、渲染帧率一类）。</summary>
    public Task<double?> ReadPlayerNumberAsync(string property, CancellationToken cancellationToken) =>
        _player.GetNumberAsync(property, cancellationToken);

    public Task<string?> ReadPlayerStringAsync(string property, CancellationToken cancellationToken) =>
        _player.GetStringAsync(property, cancellationToken);

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
                await MoveAsync(1, skipBroken: true);
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

    private async Task MoveAsync(int offset, bool skipBroken = false)
    {
        if (SelectedVideo is null || Videos.Count == 0)
        {
            return;
        }

        int index = Videos.IndexOf(SelectedVideo);
        int target = index + offset;

        if (skipBroken)
        {
            while (target >= 0 && target < Videos.Count && Videos[target].Broken)
            {
                target += offset;
            }
        }

        if (target < 0)
        {
            StatusMessage = "已经是第一条。";
            await Task.CompletedTask;
            return;
        }

        if (target >= Videos.Count)
        {
            SaveState(SelectedVideo, PositionSeconds);
            StatusMessage = "已经是最后一条。";
            RaiseSummaryIfFinished();
            await Task.CompletedTask;
            return;
        }

        SelectedVideo = Videos[target];
        RaiseSummaryIfFinished();
    }

    /// <summary>没有待处理条目时把汇总抛给界面。</summary>
    private void RaiseSummaryIfFinished()
    {
        LibrarySummary summary = BuildSummary();
        if (summary.Pending.Count > 0)
        {
            return;
        }

        StatusMessage = summary.Describe();
        SummaryRequested?.Invoke(summary);
    }

    public LibrarySummary BuildSummary() => new(
        Videos.Count,
        Videos.Count(v => v.Completed),
        Videos.Where(v => v.Broken).Select(v => v.FileName).ToArray(),
        Videos.Where(v => !v.Completed && !v.Broken).Select(v => v.FileName).ToArray());

    [RelayCommand]
    private void ShowSummary() => SummaryRequested?.Invoke(BuildSummary());

    // ---------------- 播放器事件 ----------------

    private void OnPlayerFaulted(string reason)
    {
        if (_shuttingDown)
        {
            return;
        }

        _log.Info($"播放器断开：{reason}");
        Dispatcher.UIThread.Post(() => _ = RestartPlayerAsync($"播放器断开了（{reason}），正在重启…"));
    }

    [RelayCommand]
    private Task RestartPlayer() => RestartPlayerAsync("正在重启播放器…");

    /// <summary>mpv 没了就把播放器重新拉起来，并回到当前录像与位置，不丢列表与已处理状态。</summary>
    private async Task RestartPlayerAsync(string status)
    {
        if (_hostHandle == IntPtr.Zero)
        {
            return;
        }

        VideoItemViewModel? current = SelectedVideo;
        double position = PositionSeconds;
        StatusMessage = status;

        try
        {
            await _player.DisposeAsync();
            await _player.StartAsync(_hostHandle, _naming, _operation.Token);

            if (current is not null)
            {
                await OpenVideoAtAsync(current, position);
            }

            StatusMessage = $"播放器已重启，回到「{current?.FileName ?? "无录像"}」。";
            _log.Info("播放器重启完成");
        }
        catch (Exception ex)
        {
            StatusMessage = $"播放器重启失败：{ex.Message}。可以点「重启播放器」再试。";
            _log.Info($"播放器重启失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

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
