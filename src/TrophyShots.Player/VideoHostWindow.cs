using System.Runtime.InteropServices;

namespace TrophyShots.Player;

/// <summary>
/// 交给 mpv 当 <c>--wid</c> 用的宿主子窗口：mpv 会在这个窗口里再建自己的渲染窗口。
/// 两件事由它负责：跟着 UI 布局改尺寸、把点进来的焦点弹回应用主窗口（否则按键全丢）。
/// </summary>
public sealed class VideoHostWindow : IDisposable
{
    private const string WindowClassName = "TrophyShotsVideoHost";
    private static readonly NativeMethods.WndProcCallback WindowProcedure = HandleMessage;
    private static bool _classRegistered;

    private bool _disposed;

    private VideoHostWindow(IntPtr handle)
    {
        Handle = handle;
    }

    public IntPtr Handle { get; private set; }

    /// <summary>mpv 建在该宿主窗口里的渲染窗口。</summary>
    public IntPtr VideoHandle => Handle == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetWindow(Handle, NativeMethods.GW_CHILD);

    public static VideoHostWindow Create(IntPtr parentHandle, int width, int height)
    {
        EnsureClassRegistered();

        IntPtr handle = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            null,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE
                | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS,
            0, 0, Math.Max(width, 1), Math.Max(height, 1),
            parentHandle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"创建视频宿主窗口失败：{Marshal.GetLastWin32Error()}");
        }

        return new VideoHostWindow(handle);
    }

    public void SetBounds(int x, int y, int width, int height)
    {
        if (Handle == IntPtr.Zero || _disposed)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            Handle, IntPtr.Zero, x, y,
            Math.Max(width, 1), Math.Max(height, 1),
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        ResizeVideoWindow();
    }

    /// <summary>把 mpv 的渲染窗口铺满宿主客户区。mpv 的子窗口不会随父窗口自动拉伸。</summary>
    public void ResizeVideoWindow()
    {
        IntPtr video = VideoHandle;
        if (video == IntPtr.Zero || !NativeMethods.GetClientRect(Handle, out NativeMethods.RECT client))
        {
            return;
        }

        NativeMethods.SetWindowPos(
            video, IntPtr.Zero, 0, 0,
            Math.Max(client.Width, 1), Math.Max(client.Height, 1),
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private static IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_SIZE:
                ResizeChild(hwnd);
                return IntPtr.Zero;

            case NativeMethods.WM_ERASEBKGND:
                return new IntPtr(1);

            case NativeMethods.WM_SETFOCUS:
            {
                // mpv 的嵌入窗口是 disabled 的，点击会落到这一层；焦点立刻还给主窗口。
                IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
                if (root != IntPtr.Zero && root != hwnd)
                {
                    NativeMethods.SetFocus(root);
                }

                return IntPtr.Zero;
            }

            default:
                return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private static void ResizeChild(IntPtr hostHandle)
    {
        IntPtr video = NativeMethods.GetWindow(hostHandle, NativeMethods.GW_CHILD);
        if (video == IntPtr.Zero || !NativeMethods.GetClientRect(hostHandle, out NativeMethods.RECT client))
        {
            return;
        }

        NativeMethods.SetWindowPos(
            video, IntPtr.Zero, 0, 0,
            Math.Max(client.Width, 1), Math.Max(client.Height, 1),
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private static void EnsureClassRegistered()
    {
        if (_classRegistered)
        {
            return;
        }

        var windowClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = WindowProcedure,
            hbrBackground = IntPtr.Zero,
            lpszClassName = WindowClassName,
        };

        if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            int error = Marshal.GetLastWin32Error();
            const int ErrorClassAlreadyExists = 1410;
            if (error != ErrorClassAlreadyExists)
            {
                throw new InvalidOperationException($"注册视频宿主窗口类失败：{error}");
            }
        }

        _classRegistered = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Handle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
