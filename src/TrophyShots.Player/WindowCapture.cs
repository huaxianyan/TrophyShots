using System.Runtime.InteropServices;

namespace TrophyShots.Player;

/// <summary>窗口抓屏结果：BGRA 像素，自上而下。</summary>
public sealed record CapturedFrame(int Width, int Height, byte[] Bgra);

public readonly record struct RegionStats(long TotalPixels, long NonBlackPixels, double NonBlackRatio, double MeanLuminance)
{
    public override string ToString() =>
        $"pixels={TotalPixels} nonBlack={NonBlackPixels}({NonBlackRatio:P1}) meanLum={MeanLuminance:0.0}";
}

/// <summary>
/// 用 <c>PrintWindow(PW_RENDERFULLCONTENT)</c> 抓窗口内容。
/// GDI 抓屏读不到 D3D / 硬件平面上的画面，这条路径可以，所以用它做"画面真的出来了"的证据。
/// </summary>
public static class WindowCapture
{
    public static CapturedFrame Capture(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            throw new InvalidOperationException("取窗口尺寸失败");
        }

        int width = rect.Width;
        int height = rect.Height;
        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);

            var info = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // 负数 = 自上而下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                },
            };

            bitmap = NativeMethods.CreateDIBSection(
                memoryDc, ref info, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateDIBSection 失败：{Marshal.GetLastWin32Error()}");
            }

            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            if (!NativeMethods.PrintWindow(hwnd, memoryDc, NativeMethods.PW_RENDERFULLCONTENT))
            {
                NativeMethods.PrintWindow(hwnd, memoryDc, 0);
            }

            byte[] pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return new CapturedFrame(width, height, pixels);
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    /// <summary>窗口客户区左上角相对窗口左上角的偏移（像素），用于把客户区坐标换算到抓屏坐标。</summary>
    public static (int X, int Y) ClientOriginOffset(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
        {
            return (0, 0);
        }

        var point = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(hwnd, ref point))
        {
            return (0, 0);
        }

        return (point.X - rect.Left, point.Y - rect.Top);
    }

    /// <summary>统计某个矩形区域（像素坐标）的非黑比例与平均亮度。</summary>
    public static RegionStats AnalyzeRegion(CapturedFrame frame, int x, int y, int width, int height)
    {
        int left = Math.Clamp(x, 0, Math.Max(frame.Width - 1, 0));
        int top = Math.Clamp(y, 0, Math.Max(frame.Height - 1, 0));
        int right = Math.Clamp(left + width, left + 1, frame.Width);
        int bottom = Math.Clamp(top + height, top + 1, frame.Height);

        long total = 0;
        long nonBlack = 0;
        double luminanceSum = 0;

        for (int row = top; row < bottom; row++)
        {
            int offset = (row * frame.Width + left) * 4;
            for (int column = left; column < right; column++, offset += 4)
            {
                byte blue = frame.Bgra[offset];
                byte green = frame.Bgra[offset + 1];
                byte red = frame.Bgra[offset + 2];
                double luminance = (0.0722 * blue) + (0.7152 * green) + (0.2126 * red);
                luminanceSum += luminance;
                total++;
                if (luminance > 16)
                {
                    nonBlack++;
                }
            }
        }

        return new RegionStats(total, nonBlack, total == 0 ? 0 : (double)nonBlack / total, total == 0 ? 0 : luminanceSum / total);
    }
}
