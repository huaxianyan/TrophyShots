using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TrophyShots.App.Services;
using TrophyShots.App.ViewModels;

namespace TrophyShots.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private bool _shuttingDown;

    public MainWindow()
    {
        InitializeComponent();

        AppOptions options = App.Options;
        if (options.Offscreen || options.SelfTest is not null)
        {
            // 自检不出现、不抢焦点、不动鼠标。
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(-4000, -4000);
        }

        VideoHost.HostCreated += OnVideoHostCreated;
        Timeline.AddHandler(PointerPressedEvent, OnTimelinePressed, RoutingStrategies.Tunnel);
        Timeline.AddHandler(PointerReleasedEvent, OnTimelineReleased, RoutingStrategies.Tunnel);
        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    /// <summary>自检用的只读入口：视频宿主控件与窗口句柄。</summary>
    internal Controls.MpvHostControl VideoHostControl => VideoHost;

    internal IntPtr WindowHandle => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.DirectoryPicker = PickDirectoryAsync;
        }
    }

    private void OnVideoHostCreated(object? sender, IntPtr hostHandle)
    {
        if (_viewModel is null)
        {
            return;
        }

        _ = _viewModel.AttachVideoHostAsync(hostHandle);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_viewModel is null || App.Options.SelfTest is null)
        {
            return;
        }

        await SelfTestRunner.RunAsync(this, _viewModel, App.Options, App.Log);
    }

    private async Task<string?> PickDirectoryAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "选择装着录像的目录",
                AllowMultiple = false,
            });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    // ---------------- 时间轴拖动 ----------------

    private void OnTimelinePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.IsScrubbing = true;
        }
    }

    private async void OnTimelineReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.IsScrubbing = false;
        await _viewModel.SeekToAsync(Timeline.Value);
    }

    // ---------------- 快捷键（PLAN §6.3） ----------------

    private void OnKeyDown(object? sender, KeyEventArgs e) => e.Handled = HandleShortcut(e.Key, e.KeyModifiers);

    /// <summary>
    /// 快捷键映射（PLAN §6.3）。抽成方法是为了自检能直接验证映射本身，
    /// 不必依赖操作系统把按键送进窗口。
    /// </summary>
    internal bool HandleShortcut(Key key, KeyModifiers modifiers)
    {
        if (_viewModel is null)
        {
            return false;
        }

        bool shift = modifiers.HasFlag(KeyModifiers.Shift);

        switch (key)
        {
            case Key.Left:
                _ = _viewModel.StepAsync(shift ? -10 : -1);
                return true;

            case Key.Right:
                _ = _viewModel.StepAsync(shift ? 10 : 1);
                return true;

            case Key.OemComma:
                _ = _viewModel.SeekToAsync(_viewModel.PositionSeconds - 1);
                return true;

            case Key.OemPeriod:
                _ = _viewModel.SeekToAsync(_viewModel.PositionSeconds + 1);
                return true;

            case Key.Space:
                _ = _viewModel.TogglePlayCommand.ExecuteAsync(null);
                return true;

            case Key.S:
            case Key.Enter:
                _ = _viewModel.CaptureCommand.ExecuteAsync(null);
                return true;

            case Key.Back:
                _ = _viewModel.RecaptureCommand.ExecuteAsync(null);
                return true;

            case Key.Up:
                _ = _viewModel.PreviousCommand.ExecuteAsync(null);
                return true;

            case Key.Down:
                _ = _viewModel.NextCommand.ExecuteAsync(null);
                return true;

            default:
                return false;
        }
    }

    // ---------------- 退出 ----------------

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_viewModel is null || _shuttingDown)
        {
            return;
        }

        e.Cancel = true;
        _shuttingDown = true;

        try
        {
            await _viewModel.ShutdownAsync();
        }
        catch (Exception ex)
        {
            App.Log.Info($"退出清理失败 {ex.GetType().Name}: {ex.Message}");
        }

        Close();
    }
}
