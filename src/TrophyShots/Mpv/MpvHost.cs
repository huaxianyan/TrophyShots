using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TrophyShots.Interop;

namespace TrophyShots.Mpv;

/// <summary>
/// 给 mpv 当 <c>--wid</c> 用的宿主窗口。mpv 会在这个子窗口里创建自己的渲染窗口。
/// </summary>
public sealed class MpvHost : HwndHost
{
    private const string WindowClassName = "TrophyShotsVideoHost";
    private static bool _classRegistered;
    private static readonly NativeMethods.WndProcCallback _wndProc = HostWindowProc;

    /// <summary>
    /// 宿主窗口句柄，mpv 启动时作为 <c>--wid</c> 传入；BuildWindowCore 之前为 <see cref="IntPtr.Zero"/>。
    /// </summary>
    public IntPtr HostHwnd { get; private set; }

    /// <summary>mpv 创建在该宿主窗口内部的渲染窗口，用于跟随布局调整大小。</summary>
    public IntPtr VideoHwnd => HostHwnd == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetWindow(HostHwnd, NativeMethods.GW_CHILD);

    protected override Size MeasureOverride(Size constraint)
    {
        // HwndHost 默认期望尺寸为 0，不填充布局槽位就不能随窗口伸缩。
        return new Size(
            double.IsInfinity(constraint.Width) ? 0 : constraint.Width,
            double.IsInfinity(constraint.Height) ? 0 : constraint.Height);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureClassRegistered();

        int width = Math.Max((int)Math.Round(ActualWidth), 64);
        int height = Math.Max((int)Math.Round(ActualHeight), 64);

        HostHwnd = NativeMethods.CreateWindowEx(
            0, WindowClassName, null,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE
                | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS,
            0, 0, width, height,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (HostHwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx 失败：{System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }

        return new HandleRef(this, HostHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(hwnd.Handle);
        }

        HostHwnd = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SIZE = 0x0005;
        const int WM_ERASEBKGND = 0x0014;

        if (msg == WM_SIZE)
        {
            // mpv 的子窗口不会随父窗口自动拉伸，这里显式跟随。
            ResizeVideoWindow();
        }

        if (msg == WM_ERASEBKGND)
        {
            handled = true;
            return new IntPtr(1);
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    /// <summary>把 mpv 的渲染窗口调整到宿主窗口的客户区大小。</summary>
    public void ResizeVideoWindow()
    {
        if (HostHwnd == IntPtr.Zero || !NativeMethods.GetClientRect(HostHwnd, out var rect))
        {
            return;
        }

        IntPtr video = NativeMethods.GetWindow(HostHwnd, NativeMethods.GW_CHILD);
        if (video == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            video, IntPtr.Zero, 0, 0,
            Math.Max(rect.Width, 1), Math.Max(rect.Height, 1),
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private static void EnsureClassRegistered()
    {
        if (_classRegistered)
        {
            return;
        }

        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = _wndProc,
            hbrBackground = IntPtr.Zero,
            lpszClassName = WindowClassName,
        };

        ushort atom = NativeMethods.RegisterClassEx(ref wc);
        if (atom == 0)
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (error != 1410) // ERROR_CLASS_ALREADY_EXISTS
            {
                throw new InvalidOperationException($"RegisterClassEx 失败：{error}");
            }
        }

        _classRegistered = true;
    }

    private static IntPtr HostWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 宿主窗口本身不处理任何消息，全部交给默认处理过程；
        // 需要跟随尺寸的逻辑在 HwndHost 子类里通过 WndProc 完成。
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
