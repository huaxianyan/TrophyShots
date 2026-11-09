using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TrophyShots.App.ViewModels;
using TrophyShots.Core;
using TrophyShots.Player;

namespace TrophyShots.App.Services;

/// <summary>
/// M1 端到端自检：走真实入口（打开目录 → 列表 → 打开录像 → 逐帧 → 截图 → 自动下一条 → 状态文件）。
/// 只读检查画面，不模拟键鼠、不抢前台窗口。
/// </summary>
public static class SelfTestRunner
{
    public static async Task RunAsync(
        Views.MainWindow window,
        MainWindowViewModel viewModel,
        AppOptions options,
        AppLogger log)
    {
        var results = new List<(string Name, bool Ok, string Detail)>();
        void Check(string name, bool ok, string detail)
        {
            results.Add((name, ok, detail));
            log.Info($"selftest {name}: {(ok ? "PASS" : "FAIL")} {detail}");
        }

        if (options.SelfTest == "probe")
        {
            await RunProbeAsync(window, viewModel, log, results, Check);
            Summarize(log, results);
            await Task.Delay(300);
            Dispatcher.UIThread.Post(window.Close);
            return;
        }

        if (string.IsNullOrEmpty(options.Directory))
        {
            log.Info("selftest 缺少 --directory");
            Dispatcher.UIThread.Post(window.Close);
            return;
        }

        string directory = options.Directory;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        CancellationToken token = cancellation.Token;

        try
        {
            await viewModel.OpenDirectoryAsync(directory);
            await WaitUntilAsync(() => viewModel.IsReady, TimeSpan.FromSeconds(30), token);

            Check("scan-count", viewModel.Videos.Count > 0, $"列表 {viewModel.Videos.Count} 条");
            Check("first-frame", viewModel.IsReady, $"当前 {viewModel.SelectedVideo?.FileName}");

            if (viewModel.Videos.Count >= 2)
            {
                bool ordered = NaturalPathComparer.Instance.Compare(
                    viewModel.Videos[0].FullPath, viewModel.Videos[1].FullPath) < 0;
                Check("natural-order", ordered,
                    $"{viewModel.Videos[0].FileName} < {viewModel.Videos[1].FileName}");
            }

            await Task.Delay(400);
            CheckRendering(window, log, Check);

            VideoItemViewModel first = viewModel.SelectedVideo!;
            double? frameBefore = await viewModel.ReadFrameAsync(token);
            await viewModel.StepAsync(1);
            double? frameAfter = await viewModel.ReadFrameAsync(token);
            Check("frame-step", frameBefore.HasValue && frameAfter == frameBefore + 1,
                $"帧 {frameBefore} -> {frameAfter}");

            await viewModel.SeekToAsync(1.0);
            double expected = Math.Round(1.0 * 60);
            double? frameAtOneSecond = await viewModel.ReadFrameAsync(token);
            Check("seek-one-second", frameAtOneSecond.HasValue && Math.Abs(frameAtOneSecond.Value - expected) <= 2,
                $"1 秒处帧 {frameAtOneSecond}（期望约 {expected}）");

            string? screenshotPath = viewModel.ScreenshotTargetFor(first.FullPath);
            if (screenshotPath is not null && File.Exists(screenshotPath))
            {
                File.Delete(screenshotPath);
            }

            await viewModel.CaptureCommand.ExecuteAsync(null);
            bool written = screenshotPath is not null && File.Exists(screenshotPath);
            string detail = written
                ? $"{Path.GetFileName(screenshotPath!)} {new FileInfo(screenshotPath!).Length} 字节"
                : $"未生成（期望 {screenshotPath ?? "模板算不出，只能探测"}）";
            Check("screenshot-written", written, detail);

            if (written)
            {
                using FileStream stream = File.OpenRead(screenshotPath!);
                var bitmap = new Bitmap(stream);
                Check("screenshot-resolution", bitmap.PixelSize.Width == 640 && bitmap.PixelSize.Height == 360,
                    $"{bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}");
            }

            Check("list-marked-completed", viewModel.Videos[0].Completed, viewModel.Videos[0].StatusText);
            Check("progress-text", viewModel.ProgressText == $"已处理 1 / {viewModel.Videos.Count}", viewModel.ProgressText);

            if (viewModel.Videos.Count >= 2)
            {
                await WaitUntilAsync(
                    () => viewModel.SelectedVideo?.FileName != first.FileName,
                    TimeSpan.FromSeconds(30),
                    token);
                Check("auto-advance", viewModel.SelectedVideo?.FileName != first.FileName,
                    $"下一条 {viewModel.SelectedVideo?.FileName}");
            }

            await WaitUntilAsync(() => viewModel.IsReady, TimeSpan.FromSeconds(30), token);
            ProcessingState state = ProcessingState.Load(VideoLibrary.StatePathFor(directory));
            Check("state-file", state.LastFileName == viewModel.SelectedVideo?.FileName,
                $"记录 {state.LastFileName}，当前 {viewModel.SelectedVideo?.FileName}");

            await CheckKeyboardAsync(window, viewModel, Check);
            await CheckFocusBounceAsync(window, Check);
        }
        catch (OperationCanceledException)
        {
            Check("selftest-timeout", false, "2 分钟内没跑完");
        }
        catch (Exception ex)
        {
            Check("selftest-exception", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        Summarize(log, results);
        await Task.Delay(300);
        Dispatcher.UIThread.Post(window.Close);
    }

    private static void Summarize(AppLogger log, List<(string Name, bool Ok, string Detail)> results)
    {
        int pass = results.Count(r => r.Ok);
        log.Info($"selftest summary: pass={pass} fail={results.Count - pass}");
        foreach (var (name, ok, detail) in results.Where(r => !r.Ok))
        {
            log.Info($"selftest failed-item: {name} {detail}");
        }
    }

    /// <summary>
    /// 真实素材探针：只读，不往目标目录写任何东西。用于确认真实 1440p60 素材在界面链路上
    /// 能解出首帧、能深定位（seek 到片中）、能逐帧。
    /// </summary>
    private static async Task RunProbeAsync(
        Views.MainWindow window,
        MainWindowViewModel viewModel,
        AppLogger log,
        List<(string Name, bool Ok, string Detail)> results,
        Action<string, bool, string> check)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        CancellationToken token = cancellation.Token;

        try
        {
            await WaitUntilAsync(() => viewModel.IsReady, TimeSpan.FromSeconds(30), token);
            check("probe-first-frame", viewModel.IsReady, $"当前 {viewModel.SelectedVideo?.FileName}");

            await Task.Delay(400);
            CheckRendering(window, log, check);

            await viewModel.SeekToAsync(150);
            await Task.Delay(200);
            double? frameAt150 = await viewModel.ReadFrameAsync(token);
            check("probe-deep-seek", frameAt150 is > 8900 and < 9100,
                $"150 秒处帧 {frameAt150}（60fps 期望约 9000）");

            double? before = await viewModel.ReadFrameAsync(token);
            await viewModel.StepAsync(1);
            double? after = await viewModel.ReadFrameAsync(token);
            check("probe-step", before.HasValue && after == before + 1, $"帧 {before} -> {after}");

            await viewModel.SeekToAsync(0);
            await Task.Delay(200);
            check("probe-back-to-start", true, $"回到 {viewModel.PositionText}");
        }
        catch (OperationCanceledException)
        {
            check("probe-timeout", false, "3 分钟内没跑完");
        }
        catch (Exception ex)
        {
            check("probe-exception", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 键盘映射：在 Avalonia 的路由事件里发一个 KeyDown，验证快捷键接到了命令上。
    /// 不走操作系统输入（自检窗口不激活，硬投递 WM_KEYDOWN 会被框架按"窗口未激活"丢掉）。
    /// </summary>
    /// <summary>抓一次窗口，确认视频区真的有画面（不只是 mpv 解出了帧）。</summary>
    private static void CheckRendering(Views.MainWindow window, AppLogger log, Action<string, bool, string> check)
    {
        IntPtr hwnd = window.WindowHandle;
        if (hwnd == IntPtr.Zero)
        {
            check("render-visible", false, "拿不到窗口句柄");
            return;
        }

        try
        {
            CapturedFrame frame = WindowCapture.Capture(hwnd);
            double scaling = window.RenderScaling;
            Point origin = window.VideoHostControl.TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);
            (int offsetX, int offsetY) = WindowCapture.ClientOriginOffset(hwnd);

            int x = offsetX + (int)Math.Round(origin.X * scaling);
            int y = offsetY + (int)Math.Round(origin.Y * scaling);
            int width = (int)Math.Round(window.VideoHostControl.Bounds.Width * scaling);
            int height = (int)Math.Round(window.VideoHostControl.Bounds.Height * scaling);

            RegionStats stats = WindowCapture.AnalyzeRegion(frame, x, y, width, height);
            check("render-visible", stats.NonBlackRatio > 0.005,
                $"窗口 {frame.Width}x{frame.Height} 视频区=({x},{y},{width},{height}) {stats}");
        }
        catch (Exception ex)
        {
            log.Info($"抓屏失败 {ex.GetType().Name}: {ex.Message}");
            check("render-visible", false, $"抓屏失败：{ex.Message}");
        }
    }

    private static async Task CheckKeyboardAsync(
        Views.MainWindow window,
        MainWindowViewModel viewModel,
        Action<string, bool, string> check)
    {
        double? before = await viewModel.ReadFrameAsync(CancellationToken.None);
        bool handled = window.HandleShortcut(Avalonia.Input.Key.Right, Avalonia.Input.KeyModifiers.None);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        double? after = before;
        while (watch.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(50);
            after = await viewModel.ReadFrameAsync(CancellationToken.None);
            if (after != before)
            {
                break;
            }
        }

        check("keyboard-right-arrow", handled && before.HasValue && after == before + 1,
            $"快捷键已接管={handled} 帧 {before} -> {after}，耗时 {watch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// 焦点回弹：把焦点给视频宿主窗口（等同于点了一下视频区），必须立刻弹回主窗口，
    /// 否则后续按键会全部落空。这一步不需要窗口激活，同一线程内即可验证。
    /// </summary>
    private static async Task CheckFocusBounceAsync(
        Views.MainWindow window,
        Action<string, bool, string> check)
    {
        IntPtr host = window.VideoHostControl.HostHandle;
        IntPtr main = window.WindowHandle;
        if (host == IntPtr.Zero || main == IntPtr.Zero)
        {
            check("focus-bounce", false, $"宿主=0x{host:X} 主窗口=0x{main:X}");
            return;
        }

        WindowInput.FocusWindow(host);
        await Task.Delay(150);
        IntPtr focus = WindowInput.CurrentFocus();
        check("focus-bounce", focus == main, $"焦点 0x{focus:X}（主窗口 0x{main:X}）");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            token.ThrowIfCancellationRequested();
            if (condition())
            {
                return;
            }

            await Task.Delay(100, token);
        }
    }
}
