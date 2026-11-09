using System.Runtime.InteropServices;

namespace TrophyShots.App.Services;

/// <summary>自检用：只在本进程内挪焦点，不抢用户前台窗口。</summary>
internal static class WindowInput
{
    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    public static IntPtr FocusWindow(IntPtr hWnd) => SetFocus(hWnd);

    public static IntPtr CurrentFocus() => GetFocus();
}
