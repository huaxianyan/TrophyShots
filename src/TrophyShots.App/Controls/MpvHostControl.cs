using Avalonia.Controls;
using Avalonia.Platform;
using TrophyShots.Player;

namespace TrophyShots.App.Controls;

/// <summary>
/// 把 mpv 的画面嵌进 Avalonia 布局里。Avalonia 只给这个区域一个原生句柄，
/// 真正的宿主窗口由 <see cref="VideoHostWindow"/> 创建，再交给 mpv 的 <c>--wid</c>。
/// </summary>
public sealed class MpvHostControl : NativeControlHost
{
    private VideoHostWindow? _host;

    /// <summary>原生宿主窗口就绪，播放器可以在这个句柄上启动。</summary>
    public event EventHandler<IntPtr>? HostCreated;

    public IntPtr HostHandle => _host?.Handle ?? IntPtr.Zero;


    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        (int width, int height) = DeviceSize();
        _host = VideoHostWindow.Create(parent.Handle, width, height);
        HostCreated?.Invoke(this, _host.Handle);
        return new PlatformHandle(_host.Handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _host?.Dispose();
        _host = null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (_host is null)
        {
            return;
        }

        (int width, int height) = DeviceSize();
        _host.SetBounds(0, 0, width, height);
    }

    /// <summary>Avalonia 的坐标是 DIP，宿主窗口要按设备像素给，否则 125% 缩放下视频会缩成一块。</summary>
    private (int Width, int Height) DeviceSize()
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return (
            Math.Max((int)Math.Round(Bounds.Width * scaling), 1),
            Math.Max((int)Math.Round(Bounds.Height * scaling), 1));
    }
}
