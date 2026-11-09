using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrophyShots.Capture;
using TrophyShots.Diagnostics;
using TrophyShots.Interop;
using TrophyShots.Mpv;

namespace TrophyShots;

public partial class MainWindow : Window
{
    private readonly CommandLineOptions _options;
    private readonly FileLogger _log;
    private readonly Dictionary<string, string> _properties = new();

    private MpvProcess? _mpv;
    private MpvIpc? _ipc;
    private HwndSource? _source;
    private IntPtr _windowHwnd;
    private int _focusReclaims;
    private bool _shuttingDown;
    private BitmapSource? _lastCapture;
    private int _captureIndex;
    private string _lastAction = "启动中";

    public MainWindow(CommandLineOptions options, FileLogger log)
    {
        _options = options;
        _log = log;
        InitializeComponent();

        if (options.SelfTest is not null)
        {
            // 自检不抢用户前台窗口；--offscreen 时整个窗口挪到屏幕外，用户桌面上什么都不出现。
            ShowActivated = false;
            if (options.Offscreen)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -4000;
                Top = -4000;
            }
        }
    }

    private string EvidenceDirectory => _options.EvidenceDirectory
        ?? (_options.LogPath is { } logPath
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(logPath))!, "evidence")
            : Path.Combine(Environment.CurrentDirectory, "evidence"));

    // ---------------- 启动 ----------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _windowHwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_windowHwnd);
        _source?.AddHook(WindowMessageHook);

        if (_options.SelfTest is null)
        {
            BringIntoView_Keyboard();
        }
        _log.Info($"window hwnd=0x{_windowHwnd:X}");

        try
        {
            IntPtr wid = await WaitForHostWindowAsync();
            _log.Info($"host hwnd=0x{wid:X} ({VideoHost.ActualWidth:0}x{VideoHost.ActualHeight:0} DIP)");

            _mpv = MpvProcess.Start(_options.MpvPath ?? "mpv", wid, Array.Empty<string>());
            _log.Info($"mpv started {_mpv.Describe()}");

            _ipc = await MpvIpc.ConnectAsync(_mpv.PipePath, TimeSpan.FromSeconds(10));
            _ipc.PropertyChanged += OnPropertyChanged;
            _ipc.Faulted += reason => Dispatcher.Invoke(() => _log.Info($"ipc faulted: {reason}"));
            _log.Info("ipc connected");

            foreach (string property in new[]
            {
                "filename", "width", "height", "osd-dimensions", "time-pos",
                "estimated-frame-number", "pause", "video-format", "container-fps",
            })
            {
                await _ipc.ObserveAsync(property);
            }

            if (!string.IsNullOrEmpty(_options.VideoPath))
            {
                string videoPath = Path.GetFullPath(_options.VideoPath);
                JsonElement response = await _ipc.CommandAsync("loadfile", videoPath, "replace");
                _log.Info($"loadfile {_options.VideoPath} -> {MpvIpc.DescribeResponse(response)}");
                await Task.Delay(300);
                await LogSnapshotAsync("after-load");
            }

            _lastAction = "就绪";
            UpdateStatus();

            if (!string.IsNullOrEmpty(_options.SelfTest))
            {
                await RunSelfTestAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Info($"startup failed: {ex.GetType().Name}: {ex.Message}");
            StatusText.Text = "mpv 启动失败：" + ex.Message;
        }
    }

    private async Task<IntPtr> WaitForHostWindowAsync()
    {
        for (int i = 0; i < 100; i++)
        {
            if (VideoHost.HostHwnd != IntPtr.Zero)
            {
                return VideoHost.HostHwnd;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("等待视频宿主窗口超时");
    }

    /// <summary>
    /// 点击视频区时 mpv 的子窗口会抢走系统焦点，键位就再也到不了 WPF。
    /// 这里在失去焦点的瞬间判断"新焦点是不是落在视频宿主里"，是就立刻夺回。
    /// </summary>
    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_KILLFOCUS = 0x0008;

        if (msg == WM_KILLFOCUS)
        {
            IntPtr next = wParam;
            if (next != IntPtr.Zero && (next == VideoHost.HostHwnd || next == VideoHost.VideoHwnd))
            {
                NativeMethods.SetFocus(hwnd);
                _focusReclaims++;
                _log.Info($"focus reclaim #{_focusReclaims} from=0x{next:X} to=0x{hwnd:X}");
                handled = true;
                return IntPtr.Zero;
            }
        }

        return IntPtr.Zero;
    }

    private void BringIntoView_Keyboard()
    {
        Activate();
        Keyboard.Focus(this);
        Focus();
    }

    // ---------------- 状态 ----------------

    private void OnPropertyChanged(string name, JsonElement value)
    {
        _properties[name] = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "yes",
            JsonValueKind.False => "no",
            JsonValueKind.Null => "(空)",
            _ => value.GetRawText(),
        };

        Dispatcher.Invoke(UpdateStatus);
    }

    private void UpdateStatus()
    {
        string Measure(string key, string label, string suffix = "")
            => _properties.TryGetValue(key, out string? v) ? $"{label} {v}{suffix}" : $"{label} —";

        string file = _properties.TryGetValue("filename", out string? f) ? Path.GetFileName(f) : "（未加载）";
        StatusText.Text = string.Join("   ",
            file,
            Measure("estimated-frame-number", "帧"),
            Measure("time-pos", "时间", "s"),
            Measure("width", "宽"),
            Measure("height", "高"),
            Measure("osd-dimensions", "视频区"),
            Measure("pause", "暂停"),
            "| " + _lastAction);
    }

    private async Task LogSnapshotAsync(string tag)
    {
        if (_ipc is null)
        {
            return;
        }

        var parts = new List<string>();
        foreach (string property in new[]
        {
            "filename", "width", "height", "container-fps", "video-format",
            "osd-dimensions", "time-pos", "estimated-frame-number", "pause",
        })
        {
            parts.Add($"{property}={await ReadPropertyAsync(property)}");
        }

        _log.Info($"snapshot[{tag}] " + string.Join(" ", parts));
    }

    private async Task<string> ReadPropertyAsync(string property)
    {
        if (_ipc is null)
        {
            return "n/a";
        }

        try
        {
            JsonElement response = await _ipc.CommandAsync("get_property", property);
            return MpvIpc.TryGetData(response, out JsonElement data) ? MpvIpc.DescribeResponse(response) : "unavailable";
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    // ---------------- 键盘 ----------------

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_ipc is null)
        {
            return;
        }

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        string keyName = e.Key.ToString();

        switch (e.Key)
        {
            case Key.Left:
            case Key.Right:
            {
                int direction = e.Key == Key.Right ? 1 : -1;
                int frames = direction * (shift ? 10 : 1);
                e.Handled = true;
                await StepFramesAsync(frames, keyName);
                break;
            }

            case Key.Space:
                e.Handled = true;
                await TogglePauseAsync(keyName);
                break;

            case Key.S:
                e.Handled = true;
                await ScreenshotAsync(keyName);
                break;

            case Key.F12:
                e.Handled = true;
                _log.Info($"key={keyName} foreground=0x{NativeMethods.GetForegroundWindow():X}");
                CaptureEvidence("F12");
                await LogSnapshotAsync("capture");
                break;
        }
    }

    private async Task StepFramesAsync(int frames, string keyName)
    {
        if (_ipc is null)
        {
            return;
        }

        string command = frames > 0 ? "frame-step" : "frame-back-step";
        var responses = new List<string>();
        for (int i = 0; i < Math.Abs(frames); i++)
        {
            JsonElement response = await _ipc.CommandAsync(command);
            responses.Add(MpvIpc.DescribeResponse(response));
        }

        string frame = await ReadPropertyAsync("estimated-frame-number");
        string time = await ReadPropertyAsync("time-pos");
        _lastAction = $"{(frames > 0 ? "+" : "")}{frames} 帧 → 帧 {frame}";
        _log.Info($"key={keyName} action={command}x{Math.Abs(frames)} result={string.Join(",", responses)} frame={frame} time={time}");
        UpdateStatus();
    }

    private async Task TogglePauseAsync(string keyName)
    {
        if (_ipc is null)
        {
            return;
        }

        JsonElement response = await _ipc.CommandAsync("cycle", "pause");
        string paused = await ReadPropertyAsync("pause");
        _lastAction = paused == "yes" ? "已暂停" : "播放中";
        _log.Info($"key={keyName} action=cycle pause result={MpvIpc.DescribeResponse(response)} paused={paused}");
        UpdateStatus();
    }

    private async Task ScreenshotAsync(string keyName)
    {
        if (_ipc is null)
        {
            return;
        }

        string directory = Path.Combine(EvidenceDirectory, "shots");
        string baseName = _options.VideoPath is { } video
            ? Path.GetFileNameWithoutExtension(video)
            : "frame";
        // mpv 的工作目录是它自己所在目录，路径必须给绝对路径。
        string path = Path.GetFullPath(Path.Combine(directory, baseName + ".png"));
        Directory.CreateDirectory(directory);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        JsonElement response = await _ipc.CommandAsync("screenshot-to-file", path, "video");
        string frame = await ReadPropertyAsync("estimated-frame-number");
        bool written = File.Exists(path);
        string size = written ? new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture) : "0";

        _log.Info($"key={keyName} action=screenshot-to-file path={path} result={MpvIpc.DescribeResponse(response)} written={written} bytes={size} frame={frame}");
        _lastAction = written ? $"已截图 帧 {frame}" : "截图失败";
        UpdateStatus();
    }

    // ---------------- 渲染取证 ----------------

    private void CaptureEvidence(string tag)
    {
        if (_windowHwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            BitmapSource shot = WindowCapture.Capture(_windowHwnd);
            string path = Path.Combine(EvidenceDirectory, $"window-{++_captureIndex:00}.png");
            WindowCapture.SavePng(shot, path);

            (int x, int y, int w, int h) = VideoRegionInWindowPixels();
            WindowCapture.RegionStats stats = WindowCapture.AnalyzeRegion(shot, x, y, w, h);
            double diff = _lastCapture is null ? -1 : WindowCapture.DifferenceRatio(_lastCapture, shot);

            _log.Info($"capture[{tag}] path={path} window={shot.PixelWidth}x{shot.PixelHeight} " +
                      $"videoRegion=({x},{y},{w},{h}) {WindowCapture.Describe(stats)} " +
                      $"diffVsPrev={(diff < 0 ? "n/a" : diff.ToString("P2", CultureInfo.InvariantCulture))}");

            _lastCapture = shot;
            _lastAction = $"抓屏 {Path.GetFileName(path)} 非黑 {stats.NonBlackRatio:P1}";
            UpdateStatus();
        }
        catch (Exception ex)
        {
            _log.Info($"capture[{tag}] failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private (int X, int Y, int Width, int Height) VideoRegionInWindowPixels()
    {
        Point origin = VideoHost.TranslatePoint(new Point(0, 0), this);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);

        int clientX = 0;
        int clientY = 0;
        var point = new NativeMethods.POINT { X = 0, Y = 0 };
        if (NativeMethods.GetWindowRect(_windowHwnd, out NativeMethods.RECT windowRect)
            && NativeMethods.ClientToScreen(_windowHwnd, ref point))
        {
            clientX = point.X - windowRect.Left;
            clientY = point.Y - windowRect.Top;
        }

        return (
            clientX + (int)Math.Round(origin.X * dpi.DpiScaleX),
            clientY + (int)Math.Round(origin.Y * dpi.DpiScaleY),
            (int)Math.Round(VideoHost.ActualWidth * dpi.DpiScaleX),
            (int)Math.Round(VideoHost.ActualHeight * dpi.DpiScaleY));
    }

    private async void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged && !e.HeightChanged)
        {
            return;
        }

        VideoHost.ResizeVideoWindow();
        if (_ipc is null)
        {
            return;
        }

        await Task.Delay(400);
        string osd = await ReadPropertyAsync("osd-dimensions");
        _log.Info($"resize host={VideoHost.ActualWidth:0}x{VideoHost.ActualHeight:0}DIP osd-dimensions={osd}");
    }

    // ---------------- 退出 ----------------

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shuttingDown)
        {
            return;
        }

        _log.Info($"closing host={VideoHost.ActualWidth:0}x{VideoHost.ActualHeight:0}");
        e.Cancel = true;
        _shuttingDown = true;
        await ShutdownAsync();
        Close();
    }

    private async Task ShutdownAsync()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (_ipc is not null)
        {
            try
            {
                JsonElement response = await _ipc.CommandAsync("quit");
                _log.Info($"quit sent result={MpvIpc.DescribeResponse(response)}");
            }
            catch (Exception ex)
            {
                _log.Info($"quit failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (_mpv is not null)
        {
            bool exited = await _mpv.WaitForExitAsync(TimeSpan.FromSeconds(3));
            _log.Info($"mpv exited={exited} after={stopwatch.ElapsedMilliseconds}ms pid={_mpv.ProcessId}");

            if (!exited)
            {
                _mpv.Kill();
                bool killed = await _mpv.WaitForExitAsync(TimeSpan.FromSeconds(3));
                _log.Info($"mpv kill fallback exited={killed} after={stopwatch.ElapsedMilliseconds}ms");
            }
        }

        if (_ipc is not null)
        {
            await _ipc.DisposeAsync();
            _ipc = null;
        }

        _mpv?.Dispose();
        _mpv = null;
        _log.Info("shutdown complete");
    }
}
