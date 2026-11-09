using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TrophyShots.Capture;
using TrophyShots.Interop;
using TrophyShots.Mpv;
using RegionStats = TrophyShots.Capture.WindowCapture.RegionStats;

namespace TrophyShots;

/// <summary>
/// M0 自检：全部在进程内驱动真实代码路径（IPC 命令、帧步进、截图、抓屏、缩放、焦点、退出）。
/// 用 <c>--offscreen</c> 运行时窗口在屏幕外，不遮挡也不抢焦点，用户桌面上什么都不出现。
/// 用法：<c>--selftest basic</c> / <c>--selftest focus</c> / <c>--selftest hold</c>。
/// </summary>
public partial class MainWindow
{
    private readonly List<(string Name, bool Ok, string Detail)> _selfTestResults = [];

    private void SelfCheck(string name, bool ok, string detail)
    {
        _selfTestResults.Add((name, ok, detail));
        _log.Info($"selftest {name}: {(ok ? "PASS" : "FAIL")} {detail}");
    }

    private async Task<double> FrameAsync()
    {
        JsonElement response = await _ipc!.CommandAsync("get_property", "estimated-frame-number");
        return MpvIpc.TryGetData(response, out JsonElement data) && data.ValueKind == JsonValueKind.Number
            ? data.GetDouble()
            : double.NaN;
    }

    private async Task<double> GetDoubleAsync(string property)
    {
        JsonElement response = await _ipc!.CommandAsync("get_property", property);
        return MpvIpc.TryGetData(response, out JsonElement data) && data.ValueKind == JsonValueKind.Number
            ? data.GetDouble()
            : double.NaN;
    }

    private async Task<double> GetOsdWidthAsync()
    {
        JsonElement response = await _ipc!.CommandAsync("get_property", "osd-dimensions");
        return MpvIpc.TryGetData(response, out JsonElement data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("w", out JsonElement w)
            ? w.GetDouble()
            : double.NaN;
    }

    private async Task RunSelfTestAsync()
    {
        string mode = _options.SelfTest ?? "basic";
        _log.Info($"selftest mode={mode}");

        if (mode == "hold")
        {
            // 留给外部强制结束进程，用来验证不残留 mpv。
            _log.Info("selftest hold: 等待外部结束进程");
            await Task.Delay(Timeout.Infinite);
            return;
        }

        bool firstFrame = await WaitForFirstFrameAsync(TimeSpan.FromSeconds(10));
        SelfCheck("first-frame-ready", firstFrame, firstFrame ? "首帧已解码" : "10 秒内没拿到首帧属性");
        await LogSnapshotAsync("selftest-start");

        try
        {
            await SelfTestAsync(mode);
        }
        catch (Exception ex)
        {
            SelfCheck("selftest-exception", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        int pass = _selfTestResults.Count(r => r.Ok);
        int fail = _selfTestResults.Count - pass;
        _log.Info($"selftest summary: pass={pass} fail={fail} mode={mode}");
        foreach (var (name, ok, detail) in _selfTestResults.Where(r => !r.Ok))
        {
            _log.Info($"selftest failed-item: {name} {detail}");
        }

        await Task.Delay(300);
        Close();
    }

    private async Task SelfTestAsync(string mode)
    {
        if (_ipc is null)
        {
            SelfCheck("ipc-connected", false, "IPC 未连接");
            return;
        }

        SelfCheck("ipc-connected", true, "已连接");

        // ---------- 1. 解码与元数据 ----------
        double width = await GetDoubleAsync("width");
        double height = await GetDoubleAsync("height");
        double fps = await GetDoubleAsync("container-fps");
        SelfCheck("decode-properties", width > 0 && height > 0 && fps > 0,
            $"size={width}x{height} fps={fps:0.###}");

        // ---------- 2. 跳到中间偏前的有画面对段（避开片头淡入；短视频按比例取） ----------
        double duration = await GetDoubleAsync("duration");
        double seekTarget = duration > 60 ? 30 : Math.Max(duration * 0.3, 0.5);
        bool seeked = await SeekAsync(seekTarget);
        SelfCheck("seek-into-content", seeked,
            seeked ? $"时长 {duration:0.0}s，已定位到 {seekTarget:0.0}s" : $"时长 {duration:0.0}s，seek 到 {seekTarget:0.0}s 没拿到稳定画面");

        // ---------- 3. 渲染：视频区确实有画面 ----------
        (_, string how, RegionStats stats) = await CaptureVideoAreaAsync("selftest-render");
        SelfCheck("render-visible", stats.NonBlackRatio > 0.005, $"{how} {WindowCapture.Describe(stats)}");

        // ---------- 4. 帧身份：用 mpv 自己的截图比对，不依赖估算帧号 ----------
        double frameAtStart = await FrameAsync();
        string hashAtStart = await ShotHashAsync("f00-start");

        double stepLatencyMs = await StepAndMeasureLatencyAsync(1);
        string hashAfterStep = await ShotHashAsync("f01-step");
        SelfCheck("frame-step-changes-picture", hashAtStart != hashAfterStep && hashAtStart != "missing",
            $"截图哈希 {hashAtStart} -> {hashAfterStep}");
        SelfCheck("frame-step-latency", stepLatencyMs >= 0 && stepLatencyMs < 100, $"{stepLatencyMs:0.0} ms");

        await FrameStepAsync(-1);
        string hashAfterBack = await ShotHashAsync("f02-back");
        SelfCheck("frame-backstep-restores-frame", hashAfterBack == hashAtStart,
            $"截图哈希 {hashAtStart} vs 回退后 {hashAfterBack}");

        // ---------- 5. 十帧往返 ----------
        await FrameStepAsync(10);
        string hashAfterTen = await ShotHashAsync("f10-ten");
        await FrameStepAsync(-10);
        string hashAfterTenBack = await ShotHashAsync("f20-ten-back");
        SelfCheck("ten-frames-change-picture", hashAfterTen != hashAtStart, $"{hashAtStart} -> {hashAfterTen}");
        SelfCheck("ten-frames-roundtrip", hashAfterTenBack == hashAtStart,
            $"{hashAtStart} vs 往返后 {hashAfterTenBack}");

        double counterDrift = await FrameAsync() - frameAtStart;
        _log.Info($"selftest counter-drift: 估算帧号往返后偏差 {counterDrift:0}（estimated-frame-number 由时间推算，仅供参考）");

        // ---------- 6. 截图落盘：原分辨率 PNG ----------
        string shotPath = Path.GetFullPath(Path.Combine(EvidenceDirectory, "shots", "selftest-frame.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(shotPath)!);
        if (File.Exists(shotPath))
        {
            File.Delete(shotPath);
        }

        JsonElement shotResponse = await _ipc.CommandAsync("screenshot-to-file", shotPath, "video");
        bool shotWritten = File.Exists(shotPath);
        string shotDetail = $"未生成（{MpvIpc.DescribeResponse(shotResponse)}）";
        if (shotWritten)
        {
            long bytes = new FileInfo(shotPath).Length;
            using FileStream stream = File.OpenRead(shotPath);
            BitmapFrame png = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            shotDetail = $"png={png.PixelWidth}x{png.PixelHeight} bytes={bytes}";
            SelfCheck("screenshot-resolution", png.PixelWidth == (int)width && png.PixelHeight == (int)height, shotDetail);
        }

        SelfCheck("screenshot-written", shotWritten, shotDetail);

        // ---------- 7. 窗口缩放，mpv 画面跟随 ----------
        double osdWidthBefore = await GetOsdWidthAsync();
        Width = 1000;
        Height = 700;
        await Task.Delay(900);
        double osdWidthAfter = await GetOsdWidthAsync();
        SelfCheck("resize-follows", osdWidthAfter > 0 && Math.Abs(osdWidthAfter - osdWidthBefore) > 50,
            $"osd 宽 {osdWidthBefore:0} -> {osdWidthAfter:0}");

        (_, _, RegionStats statsResized) = await CaptureVideoAreaAsync("selftest-resized");
        SelfCheck("resize-still-rendering", statsResized.NonBlackRatio > 0.005, WindowCapture.Describe(statsResized));

        Width = 1320;
        Height = 820;
        await Task.Delay(600);

        // ---------- 8. 键盘与焦点需要窗口是活动窗口，只在 focus 模式跑 ----------
        if (!mode.Contains("focus", StringComparison.OrdinalIgnoreCase))
        {
            _log.Info("selftest keyboard/focus: SKIP（basic 模式不动前台窗口；用 --selftest focus 跑）");
            return;
        }

        IntPtr previousForeground = NativeMethods.GetForegroundWindow();
        try
        {
            ActivateWindowForInput();

            double frameBeforeKey = await FrameAsync();
            double keyLatencyMs = await PressRightViaWindowMessageAsync();
            double frameAfterKey = await FrameAsync();
            SelfCheck("keyboard-message-path", double.IsFinite(keyLatencyMs) && keyLatencyMs < 200,
                $"帧 {frameBeforeKey} -> {frameAfterKey}，处理耗时 {keyLatencyMs:0.0} ms");
            SelfCheck("keyboard-advances-one-frame", frameAfterKey == frameBeforeKey + 1,
                $"{frameBeforeKey} -> {frameAfterKey}");

            await SelfTestFocusReclaimAsync();
        }
        finally
        {
            RestoreForeground(previousForeground);
        }
    }

    // ---------------- 帧与截图辅助 ----------------

    private async Task FrameStepAsync(int frames)
    {
        for (int i = 0; i < Math.Abs(frames); i++)
        {
            await StepOnceAsync(frames > 0);
        }

        await Task.Delay(120);
    }

    /// <summary>
    /// 单步并等待画面真的动过。连发多条 frame-back-step 时 mpv 会丢掉 seek 期间到达的请求，
    /// 所以每一步都要确认 time-pos 变了再发下一条。
    /// </summary>
    private async Task StepOnceAsync(bool forward)
    {
        string command = forward ? "frame-step" : "frame-back-step";
        double before = await GetDoubleAsync("time-pos");
        await _ipc!.CommandAsync(command);

        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 400)
        {
            await Task.Delay(15);
            double now = await GetDoubleAsync("time-pos");
            if (double.IsFinite(now) && now != before)
            {
                return;
            }
        }

        _log.Info($"step-settle 超时：{command} 之后 time-pos 未变化（before={before:0.000}）");
    }

    /// <summary>步进一帧，并测量"发出命令到 time-pos 变化"的延迟。</summary>
    private async Task<double> StepAndMeasureLatencyAsync(int frames)
    {
        var probe = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTimePos(string name, JsonElement value)
        {
            if (name == "time-pos" && value.ValueKind == JsonValueKind.Number)
            {
                probe.TrySetResult(value.GetDouble());
            }
        }

        _ipc!.PropertyChanged += OnTimePos;
        var watch = Stopwatch.StartNew();
        string command = frames > 0 ? "frame-step" : "frame-back-step";
        try
        {
            for (int i = 0; i < Math.Abs(frames); i++)
            {
                await _ipc.CommandAsync(command);
            }

            await probe.Task.WaitAsync(TimeSpan.FromSeconds(2));
            return watch.Elapsed.TotalMilliseconds;
        }
        catch (TimeoutException)
        {
            return -1;
        }
        finally
        {
            _ipc.PropertyChanged -= OnTimePos;
        }
    }

    /// <summary>
    /// 让 mpv 自己把当前帧截成原分辨率 PNG，取哈希。
    /// 同一帧的两次截图字节一致，所以哈希相等 = 画面回到同一帧，比估算帧号可靠。
    /// </summary>
    private async Task<string> ShotHashAsync(string name)
    {
        string path = Path.GetFullPath(Path.Combine(EvidenceDirectory, "shots", name + ".png"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        JsonElement response = await _ipc!.CommandAsync("screenshot-to-file", path, "video");
        if (!File.Exists(path))
        {
            _log.Info($"shot[{name}] 失败：{MpvIpc.DescribeResponse(response)}");
            return "missing";
        }

        using FileStream stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream))[..16];
        _log.Info($"shot[{name}] hash={hash} bytes={stream.Length} frame={await FrameAsync()}");
        return hash;
    }

    private async Task<bool> SeekAsync(double seconds)
    {
        await _ipc!.CommandAsync("seek", seconds, "absolute+exact");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            double position = await GetDoubleAsync("time-pos");
            if (double.IsFinite(position) && Math.Abs(position - seconds) < 0.5 && double.IsFinite(await FrameAsync()))
            {
                await Task.Delay(150);
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>
    /// 两条互不依赖的抓屏路径各取一次：整窗 PrintWindow（不受遮挡影响，但可能抓不到 D3D 子窗口）、
    /// 以及直接抓 mpv 渲染窗口。报出两者里更可信的那份。
    /// </summary>
    private async Task<(BitmapSource Bitmap, string How, RegionStats Stats)> CaptureVideoAreaAsync(string tag)
    {
        await Task.Delay(200);

        (int x, int y, int w, int h) = VideoRegionInWindowPixels();
        BitmapSource windowShot = WindowCapture.Capture(_windowHwnd);
        WindowCapture.SavePng(windowShot, Path.GetFullPath(Path.Combine(EvidenceDirectory, $"window-{tag}.png")));
        RegionStats windowStats = WindowCapture.AnalyzeRegion(windowShot, x, y, w, h);
        _log.Info($"capture[{tag}] window {WindowCapture.Describe(windowStats)}");

        BitmapSource? videoShot = null;
        RegionStats videoStats = new(0, 0, 0, 0);
        IntPtr videoHwnd = VideoHost.VideoHwnd;
        if (videoHwnd != IntPtr.Zero)
        {
            videoShot = WindowCapture.Capture(videoHwnd);
            WindowCapture.SavePng(videoShot, Path.GetFullPath(Path.Combine(EvidenceDirectory, $"video-{tag}.png")));
            videoStats = WindowCapture.AnalyzeRegion(videoShot, 0, 0, videoShot.PixelWidth, videoShot.PixelHeight);
            _log.Info($"capture[{tag}] video {WindowCapture.Describe(videoStats)}");
        }

        if (videoStats.NonBlackRatio > windowStats.NonBlackRatio)
        {
            return (videoShot!, "video-window", videoStats);
        }

        return (windowShot, "window", windowStats);
    }

    /// <summary>给主窗口投递一对 WM_KEYDOWN/WM_KEYUP，走 WPF 输入管线到我们自己的快捷键处理。</summary>
    private async Task<double> PressRightViaWindowMessageAsync()
    {
        const int WM_KEYDOWN = 0x0100;
        const int WM_KEYUP = 0x0101;
        int vkRight = 0x27;

        var watch = Stopwatch.StartNew();
        double before = await FrameAsync();
        NativeMethods.PostMessage(_windowHwnd, WM_KEYDOWN, new IntPtr(vkRight), new IntPtr(1));
        NativeMethods.PostMessage(_windowHwnd, WM_KEYUP, new IntPtr(vkRight), unchecked((IntPtr)0xC0000001));

        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(25);
            if (await FrameAsync() != before)
            {
                return watch.Elapsed.TotalMilliseconds;
            }
        }

        return double.NaN;
    }

    /// <summary>
    /// 模拟"点击视频区"造成的焦点转移。mpv 0.32 的嵌入窗口是 disabled + WS_EX_NOACTIVATE，
    /// 点击真正落到的焦点窗口是它上面的宿主窗口，这里就按这个真实路径验证主窗口能否立刻夺回。
    /// </summary>
    private async Task SelfTestFocusReclaimAsync()
    {
        IntPtr videoHwnd = VideoHost.VideoHwnd;
        IntPtr hostHwnd = VideoHost.HostHwnd;
        if (hostHwnd == IntPtr.Zero || videoHwnd == IntPtr.Zero)
        {
            SelfCheck("focus-window-handles", false, $"host=0x{hostHwnd:X} video=0x{videoHwnd:X}");
            return;
        }

        _log.Info($"focus-test windows host=0x{hostHwnd:X} video=0x{videoHwnd:X} " +
                  $"videoEnabled={NativeMethods.IsWindowEnabled(videoHwnd)}");

        int reclaimsBefore = _focusReclaims;
        NativeMethods.SetFocus(hostHwnd);
        await Task.Delay(250);

        IntPtr focusAfter = NativeMethods.GetFocus();
        SelfCheck("focus-reclaim-fired", _focusReclaims > reclaimsBefore,
            $"夺回次数 {reclaimsBefore} -> {_focusReclaims}");
        SelfCheck("focus-back-on-main-window", focusAfter == _windowHwnd,
            $"GetFocus=0x{focusAfter:X} 主窗口=0x{_windowHwnd:X}");

        double frameBefore = await FrameAsync();
        double latency = await PressRightViaWindowMessageAsync();
        double frameAfter = await FrameAsync();
        SelfCheck("keyboard-after-focus-shift", frameAfter == frameBefore + 1 && double.IsFinite(latency),
            $"{frameBefore} -> {frameAfter}，处理耗时 {latency:0.0} ms");
    }

    private async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (double.IsFinite(await FrameAsync()) && double.IsFinite(await GetDoubleAsync("width")))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>把本窗口设为前台。用于需要真实输入焦点的自检，结束后必须恢复原前台窗口。</summary>
    private void ActivateWindowForInput()
    {
        uint ourThread = NativeMethods.GetCurrentThreadId();
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        uint foregroundThread = foreground == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        bool attached = foregroundThread != 0 && foregroundThread != ourThread
            && NativeMethods.AttachThreadInput(ourThread, foregroundThread, true);
        try
        {
            NativeMethods.SetForegroundWindow(_windowHwnd);
            NativeMethods.SetFocus(_windowHwnd);
            Activate();
            Keyboard.Focus(this);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(ourThread, foregroundThread, false);
            }
        }
    }

    private static void RestoreForeground(IntPtr previous)
    {
        if (previous != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(previous);
        }
    }
}
