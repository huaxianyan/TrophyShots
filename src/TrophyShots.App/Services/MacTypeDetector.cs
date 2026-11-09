using System.Runtime.InteropServices;

namespace TrophyShots.App.Services;

/// <summary>
/// 判断本进程里有没有被 MacType 注入字体 hook。
/// 本机 MacType 的 GDI hook 会让"用 GDI 画字的系统对话框"把进程在 gdi32full.dll 里 fast-fail 崩溃
/// （2026-11-10 实测：选目录按钮一点就 BEX64 c0000409）。所以系统文件夹对话框只在没被注入时用。
/// </summary>
internal static class MacTypeDetector
{
    private static readonly string[] ModuleNames = ["MacType64.dll", "MacType.dll", "Easyhk64.dll", "EasyHK32.dll"];

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    public static bool IsInjected() =>
        ModuleNames.Any(name => GetModuleHandleW(name) != IntPtr.Zero);
}
