using System.Diagnostics;
using System.Globalization;

namespace TrophyShots.Player;

public sealed record MediaProbeResult(TimeSpan? Duration, double? FrameRate, bool IsVariableFrameRate)
{
    public static readonly MediaProbeResult Unknown = new(null, null, false);
}

/// <summary>
/// 用 ffprobe 读时长与帧率。只读元数据，毫秒级；不逐帧扫描（PLAN §4：逐帧扫描一个文件要 72 秒）。
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

        // CFR 判定：两个帧率一致（含都在误差内）才算恒定帧率，否则逐帧可能不精确。
        bool variable = frameRate.HasValue && averageFrameRate.HasValue
            && Math.Abs(frameRate.Value - averageFrameRate.Value) > 0.01;

        return new MediaProbeResult(
            duration.HasValue ? TimeSpan.FromSeconds(duration.Value) : null,
            frameRate ?? averageFrameRate,
            variable);
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
