using System.Diagnostics;
using System.Globalization;

namespace TrophyShots.Player;

public sealed record MediaProbeResult(
    TimeSpan? Duration,
    double? FrameRate,
    bool IsVariableFrameRate,
    int SampleCount = 0)
{
    public static readonly MediaProbeResult Unknown = new(null, null, false);
}

/// <summary>
/// 用 ffprobe 读时长、帧率，并判断是不是非常数帧率。只读元数据与开头几秒的包时间戳，
/// 毫秒级；不逐帧扫描（PLAN §4：逐帧扫描一个文件要 72 秒）。
/// </summary>
public static class MediaProbe
{
    public static async Task<MediaProbeResult> ProbeAsync(
        string ffprobePath,
        string videoPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffprobePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-select_streams");
        startInfo.ArgumentList.Add("v:0");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("stream=r_frame_rate,avg_frame_rate:format=duration");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1");
        startInfo.ArgumentList.Add(videoPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return MediaProbeResult.Unknown;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        string output;
        try
        {
            output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return MediaProbeResult.Unknown;
        }

        if (process.ExitCode != 0)
        {
            return MediaProbeResult.Unknown;
        }

        double? duration = null;
        double? frameRate = null;
        double? averageFrameRate = null;
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator];
            string value = line[(separator + 1)..];
            switch (key)
            {
                case "duration":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                    {
                        duration = seconds;
                    }

                    break;
                case "r_frame_rate":
                    frameRate = ParseRational(value);
                    break;
                case "avg_frame_rate":
                    averageFrameRate = ParseRational(value);
                    break;
            }
        }

        // 先看开头几秒的真实包时间戳。mkv 里 r_frame_rate 与 avg_frame_rate 常常都报同一个值
        // （2026-11-10 实测：真变帧率文件的 PTS 间隔 0.017/0.033/0.083 跳动，ffprobe 仍报 60/60），
        // 所以两个帧率相等并不能证明是 CFR，只能作为兜底信号。
        IReadOnlyList<double>? sampledTimes = await SamplePresentationTimesAsync(ffprobePath, videoPath, cancellationToken)
            .ConfigureAwait(false);

        bool variable = sampledTimes is { Count: > 0 }
            ? LooksVariableFrameRate(sampledTimes)
            : frameRate.HasValue && averageFrameRate.HasValue
                && Math.Abs(frameRate.Value - averageFrameRate.Value) > 0.01;

        return new MediaProbeResult(
            duration.HasValue ? TimeSpan.FromSeconds(duration.Value) : null,
            frameRate ?? averageFrameRate,
            variable,
            sampledTimes?.Count ?? 0);
    }

    /// <summary>读开头约 5 秒的包时间戳（不解码），用来判断帧间隔是否均匀。</summary>
    public static async Task<IReadOnlyList<double>?> SamplePresentationTimesAsync(
        string ffprobePath,
        string videoPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffprobePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-select_streams");
        startInfo.ArgumentList.Add("v:0");
        startInfo.ArgumentList.Add("-read_intervals");
        startInfo.ArgumentList.Add("%+5");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("packet=pts_time");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("csv=p=0");
        startInfo.ArgumentList.Add(videoPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return null;
            }

            var times = new List<double>();
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                {
                    times.Add(seconds);
                }
            }

            return times;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return null;
        }
    }

    /// <summary>
    /// 帧间隔是否均匀：排序后求相邻差值的中位数，偏离中位数太多的占比过多就判非常数帧率。
    /// 包顺序是解码顺序（B 帧会乱序），必须先排序再求差。
    /// 容差要留出时间戳量化余量：时间基是 1/1000 时，1/60 会在 0.016/0.017 之间来回跳，
    /// 单纯按 5% 相对误差判会把所有 CFR 素材全判成变帧率（2026-11-10 实测踩到）。
    /// </summary>
    public static bool LooksVariableFrameRate(IReadOnlyList<double> presentationTimes)
    {
        double[] sorted = presentationTimes.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length < 8)
        {
            return false;
        }

        var deltas = new List<double>(sorted.Length - 1);
        for (int i = 1; i < sorted.Length; i++)
        {
            double delta = sorted[i] - sorted[i - 1];
            if (delta > 1e-6)
            {
                deltas.Add(delta);
            }
        }

        if (deltas.Count < 6)
        {
            return false;
        }

        double[] ordered = deltas.Order().ToArray();
        double median = ordered[ordered.Length / 2];
        if (median <= 0)
        {
            return false;
        }

        double tolerance = Math.Max(0.0025, median * 0.25);
        int outliers = deltas.Count(delta => Math.Abs(delta - median) > tolerance);
        return outliers > deltas.Count * 0.05;
    }

    private static double? ParseRational(string value)
    {
        int slash = value.IndexOf('/');
        if (slash > 0
            && double.TryParse(value[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator)
            && double.TryParse(value[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator)
            && denominator != 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double single) && single > 0
            ? single
            : null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 进程可能已经结束。
        }
    }
}
