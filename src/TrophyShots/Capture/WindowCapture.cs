using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrophyShots.Interop;

namespace TrophyShots.Capture;

/// <summary>
/// 把整个窗口（含 mpv 的子窗口）抓成位图，用于自动核对"视频区真的画出了画面"。
/// 走 <c>PrintWindow(PW_RENDERFULLCONTENT)</c>，不依赖 GDI 抓屏（读不到 D3D/硬件平面内容）。
/// </summary>
public static class WindowCapture
{
    public sealed record RegionStats(long TotalPixels, long NonBlackPixels, double NonBlackRatio, double MeanLuminance);

    public static BitmapSource Capture(IntPtr hwnd)
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
                    biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
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
                throw new InvalidOperationException($"CreateDIBSection 失败：{System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            }

            previous = NativeMethods.SelectObject(memoryDc, bitmap);

            if (!NativeMethods.PrintWindow(hwnd, memoryDc, NativeMethods.PW_RENDERFULLCONTENT))
            {
                // 部分窗口不支持 FULLCONTENT，退化为普通模式。
                NativeMethods.PrintWindow(hwnd, memoryDc, 0);
            }

            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            System.Runtime.InteropServices.Marshal.Copy(bits, pixels, 0, pixels.Length);

            BitmapSource source = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            source.Freeze();
            return source;
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

    public static void SavePng(BitmapSource source, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>统计某一矩形区域（窗口内像素坐标）的非黑像素比例与平均亮度。</summary>
    public static RegionStats AnalyzeRegion(BitmapSource source, int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, source.PixelWidth - 1);
        y = Math.Clamp(y, 0, source.PixelHeight - 1);
        width = Math.Clamp(width, 1, source.PixelWidth - x);
        height = Math.Clamp(height, 1, source.PixelHeight - y);

        var cropped = new CroppedBitmap(source, new System.Windows.Int32Rect(x, y, width, height));
        var converted = new FormatConvertedBitmap(cropped, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        long total = converted.PixelWidth * converted.PixelHeight;
        long nonBlack = 0;
        double sum = 0;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            double luminance = 0.0722 * b + 0.7152 * g + 0.2126 * r;
            sum += luminance;
            if (luminance > 16)
            {
                nonBlack++;
            }
        }

        return new RegionStats(total, nonBlack, total == 0 ? 0 : (double)nonBlack / total, total == 0 ? 0 : sum / total);
    }

    /// <summary>两张同尺寸位图逐像素求差异比例（亮度差超过阈值的像素占比）。</summary>
    public static double DifferenceRatio(BitmapSource a, BitmapSource b, double threshold = 8)
    {
        byte[] pa = ReadBgra(a);
        byte[] pb = ReadBgra(b);
        if (pa.Length != pb.Length)
        {
            return 1;
        }

        long different = 0;
        long total = pa.Length / 4;
        for (int i = 0; i < pa.Length; i += 4)
        {
            int delta = Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]);
            if (delta / 3.0 > threshold)
            {
                different++;
            }
        }

        return total == 0 ? 0 : (double)different / total;
    }

    private static byte[] ReadBgra(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    public static string Describe(RegionStats stats)
    {
        var sb = new StringBuilder();
        sb.Append("pixels=").Append(stats.TotalPixels);
        sb.Append(" nonBlack=").Append(stats.NonBlackPixels);
        sb.Append('(').Append(stats.NonBlackRatio.ToString("P1")).Append(')');
        sb.Append(" meanLum=").Append(stats.MeanLuminance.ToString("0.0"));
        return sb.ToString();
    }
}
