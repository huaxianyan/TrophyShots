using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace TrophyShots.App.Services;

/// <summary>
/// 列表缩略图：用 ffmpeg 从片头往后一点截一帧，缩到 176 宽，缓存到本机。
/// 缓存键含文件大小与修改时间，录像被换掉时会自动重新生成。
/// </summary>
public sealed class ThumbnailService
{
    private const int ThumbnailWidth = 176;
    private const double SamplePositionRatio = 0.1;

    private readonly string _ffmpegPath;
    private readonly AppLogger _log;
    private readonly string _cacheDirectory;

    public ThumbnailService(AppOptions options, AppLogger log)
    {
        _ffmpegPath = options.FfmpegPath;
        _log = log;
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TrophyShots",
            "thumbnails");
    }

    public async Task<Bitmap?> GetAsync(string videoPath, TimeSpan duration, CancellationToken cancellationToken)
    {
        string cachePath = CachePathFor(videoPath);

        try
        {
            if (File.Exists(cachePath))
            {
                return Load(cachePath);
            }

            Directory.CreateDirectory(_cacheDirectory);

            double at = Math.Max(duration.TotalSeconds * SamplePositionRatio, 1.0);
            string temporary = cachePath + ".tmp.jpg";
            if (!await ExtractAsync(videoPath, at, temporary, cancellationToken).ConfigureAwait(false))
            {
                TryDelete(temporary);
                return null;
            }

            File.Move(temporary, cachePath, overwrite: true);
            return Load(cachePath);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Info($"缩略图失败 {Path.GetFileName(videoPath)} {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> ExtractAsync(string videoPath, double at, string outputPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (string argument in new[]
        {
            "-y", "-loglevel", "error",
            "-ss", at.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "-i", videoPath,
            "-frames:v", "1",
            "-vf", $"scale={ThumbnailWidth}:-2",
            "-q:v", "5",
            outputPath,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return false;
        }

        return process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }

    private static Bitmap Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        return new Bitmap(buffer);
    }

    private string CachePathFor(string videoPath)
    {
        FileInfo info = new(videoPath);
        string key = $"{Path.GetFullPath(videoPath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_cacheDirectory, hash[..16] + ".jpg");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }
    }
}
