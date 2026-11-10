using System.Diagnostics;
using System.Globalization;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace TrophyShots.Player;

/// <summary>
/// 跳奖杯检测：靠平台固定文案（`赢得奖杯` / `トロフィー` / `Trophy`）认横幅，与游戏内容无关。
/// 两遍法——关键帧粗扫全片，命中点附近再按 10fps 精定位，最后按文案变化切段。
/// </summary>
public sealed class TrophyDetector
{
    private const string CropFilter = "crop=iw*0.52:ih*0.20:iw*0.48:0,scale=iw*0.5:-2";
    private const double FineWindowSeconds = 8;
    private const double FineFps = 10;
    private const double CoarseClusterGapSeconds = 8;
    private const double SegmentGapSeconds = 1.0;

    private static readonly Lazy<OcrEngine[]> Engines = new(CreateEngines);

    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;

    public TrophyDetector(string ffmpegPath, string ffprobePath)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
    }

    public static bool IsSupported => Engines.Value.Length > 0;

    /// <summary>检测一条录像，返回按时间正序的奖杯片段（可能为空）。</summary>
    public async Task<IReadOnlyList<TrophySegment>> DetectAsync(
        string videoPath,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        OcrEngine[] engines = Engines.Value;
        if (engines.Length == 0)
        {
            return Array.Empty<TrophySegment>();
        }

        string tempDirectory = Directory.CreateTempSubdirectory("trophyshots-trophy-").FullName;
        try
        {
            progress?.Invoke("扫描关键帧");
            string coarseDirectory = Path.Combine(tempDirectory, "coarse");
            Directory.CreateDirectory(coarseDirectory);
            List<double> coarseTimes = await ExtractFramesAsync(
                videoPath, coarseDirectory, from: null, duration: null, fps: null, cancellationToken).ConfigureAwait(false);

            string[] coarseFrames = Directory.GetFiles(coarseDirectory, "*.png");
            Array.Sort(coarseFrames, StringComparer.Ordinal);

            var coarseHits = new List<(double Time, string Text)>();
            for (int i = 0; i < coarseFrames.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (bool hit, string text) = await RecognizeAsync(coarseFrames[i], engines, cancellationToken).ConfigureAwait(false);
                if (hit)
                {
                    coarseHits.Add((i < coarseTimes.Count ? coarseTimes[i] : -1, text));
                }
            }

            foreach (var hit in coarseHits)
            {
                progress?.Invoke($"粗扫命中 {hit.Time:0.00}s");
            }

            if (coarseHits.Count == 0)
            {
                // 关键帧间隔约 4 秒，极短的横幅可能整段落在两个关键帧之间。
                // 用户习惯"跳完奖杯立刻保存"，所以只对片尾再补扫一遍，代价很小。
                progress?.Invoke("关键帧没命中，补扫片尾");
                return await ScanTailAsync(videoPath, tempDirectory, engines, cancellationToken).ConfigureAwait(false);
            }

            progress?.Invoke($"正在定位 {coarseHits.Count} 处候选");
            var segments = new List<TrophySegment>();
            foreach (List<double> cluster in Cluster(coarseHits.Select(h => h.Time)))
            {
                double from = Math.Max(0, cluster[0] - FineWindowSeconds);
                double to = cluster[^1] + FineWindowSeconds;
                string fineDirectory = Path.Combine(tempDirectory, "fine-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(fineDirectory);
                List<double> fineTimes = await ExtractFramesAsync(
                    videoPath, fineDirectory, from, to - from, FineFps, cancellationToken).ConfigureAwait(false);

                string[] fineFrames = Directory.GetFiles(fineDirectory, "*.png");
                Array.Sort(fineFrames, StringComparer.Ordinal);

                var hits = new List<(double Time, string Text)>();
                for (int i = 0; i < fineFrames.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (bool hit, string text) = await RecognizeAsync(fineFrames[i], engines, cancellationToken).ConfigureAwait(false);
                    if (hit)
                    {
                        double at = i < fineTimes.Count ? fineTimes[i] : from + i / FineFps;
                        hits.Add((at, text));
                    }
                }

                progress?.Invoke($"精定位 {from:0.00}s 起 {fineFrames.Length} 帧，命中 {hits.Count} 帧");
                segments.AddRange(SplitByText(hits));
            }

            return segments.OrderBy(s => s.StartSeconds).ToList();
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>片尾补扫：按 2fps 解最后 60 秒，命中则同样进精定位。</summary>
    private async Task<IReadOnlyList<TrophySegment>> ScanTailAsync(
        string videoPath,
        string tempDirectory,
        OcrEngine[] engines,
        CancellationToken cancellationToken)
    {
        const double TailSeconds = 60;
        const double TailFps = 2;

        string directory = Path.Combine(tempDirectory, "tail");
        Directory.CreateDirectory(directory);
        List<double> tailTimes = await ExtractFramesAsync(
            videoPath, directory, null, null, TailFps, cancellationToken, tailSeconds: TailSeconds).ConfigureAwait(false);

        string[] frames = Directory.GetFiles(directory, "*.png");
        Array.Sort(frames, StringComparer.Ordinal);

        var hits = new List<(double Time, string Text)>();
        for (int i = 0; i < frames.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (bool hit, string text) = await RecognizeAsync(frames[i], engines, cancellationToken).ConfigureAwait(false);
            if (hit)
            {
                double at = i < tailTimes.Count ? tailTimes[i] : 0;
                hits.Add((at, text));
            }
        }

        if (hits.Count == 0)
        {
            return Array.Empty<TrophySegment>();
        }

        return SplitByText(hits);
    }

    /// <summary>
    /// 连续命中里切段。两步走：
    /// 1) 按文案分组，相邻同文案的帧归一组；
    /// 2) 持续时间太短的组并到邻居——横幅滑入的那几百毫秒 OCR 只能读到半截文字，
    ///    这些碎片不是新奖杯，真正的另一枚奖杯会稳定显示好几秒。
    /// </summary>
    private static List<TrophySegment> SplitByText(List<(double Time, string Text)> hits)
    {
        var segments = new List<TrophySegment>();
        if (hits.Count == 0)
        {
            return segments;
        }

        var groups = new List<(double Start, double End, string Title, int Frames)>();
        foreach ((double time, string text) in hits)
        {
            string title = Title(text);
            if (groups.Count > 0
                && time - groups[^1].End <= SegmentGapSeconds
                && SameTrophy(groups[^1].Title, title))
            {
                groups[^1] = groups[^1] with { End = time, Frames = groups[^1].Frames + 1 };
            }
            else
            {
                groups.Add((time, time, title, 1));
            }
        }

        const double MinSeconds = 1.5;
        bool mergedAny = true;
        while (mergedAny && groups.Count > 1)
        {
            mergedAny = false;
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i].End - groups[i].Start >= MinSeconds)
                {
                    continue;
                }

                int target = i + 1 < groups.Count ? i + 1 : i - 1;
                (double Start, double End, string Title, int Frames) keep = groups[target];
                (double Start, double End, string Title, int Frames) drop = groups[i];
                groups[target] = (
                    Math.Min(keep.Start, drop.Start),
                    Math.Max(keep.End, drop.End),
                    keep.Frames >= drop.Frames ? keep.Title : drop.Title,
                    keep.Frames + drop.Frames);
                groups.RemoveAt(i);
                mergedAny = true;
                break;
            }
        }

        var merged = groups.Select(g => new TrophySegment(g.Start, g.End, g.Title)).ToList();

        // 同一个横幅被切开的最后一道闸：标题一字不差、间隔不到 0.6 秒 → 并回一段。
        for (int i = merged.Count - 1; i > 0; i--)
        {
            if (merged[i].Text == merged[i - 1].Text
                && merged[i].StartSeconds - merged[i - 1].EndSeconds <= 0.6)
            {
                merged[i - 1] = merged[i - 1] with { EndSeconds = merged[i].EndSeconds };
                merged.RemoveAt(i);
            }
        }

        return merged;
    }

    /// <summary>取奖杯名：`得奖杯` 之前的部分，去掉 OCR 常掺进来的数字/拉丁字符。</summary>
    private static string Title(string text)
    {
        string flat = Normalize(text);
        int cut = flat.IndexOf("得奖杯", StringComparison.Ordinal);
        if (cut < 0)
        {
            cut = flat.IndexOf("トロフィー", StringComparison.Ordinal);
        }

        if (cut > 0)
        {
            flat = flat[..cut];
        }

        string title = new string(flat.Where(c => !char.IsAsciiDigit(c) && !char.IsAsciiLetter(c)).ToArray());
        return title.TrimEnd('赢', '贏', '羸', '嬴', '得', '奖', '杯', '獎', '盃');
    }

    private static bool SameTrophy(string previous, string current)
    {
        if (previous.Length == 0 || current.Length == 0)
        {
            return true;
        }

        if (previous.Equals(current, StringComparison.Ordinal))
        {
            return true;
        }

        // 繁体/简体、异体字在 OCR 里会来回变（亞/亚、贏/赢），按字符集重合度比更稳。
        HashSet<char> a = previous.ToHashSet();
        HashSet<char> b = current.ToHashSet();
        int shared = a.Count(b.Contains);
        int union = a.Count + b.Count - shared;
        return union > 0 && (double)shared / union >= 0.55;
    }

    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        int shared = 0;
        foreach (char c in a.Distinct())
        {
            if (b.Contains(c))
            {
                shared++;
            }
        }

        return (double)shared / Math.Max(a.Distinct().Count(), b.Distinct().Count());
    }

    private static string Normalize(string text) =>
        new(text.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray());

    /// <summary>按时间间隔把命中点分簇，间隔过大说明是两处不同的奖杯。</summary>
    private static IEnumerable<List<double>> Cluster(IEnumerable<double> times)
    {
        List<double>? current = null;
        foreach (double time in times.Order())
        {
            if (current is not null && time - current[^1] <= CoarseClusterGapSeconds)
            {
                current.Add(time);
                continue;
            }

            if (current is not null)
            {
                yield return current;
            }

            current = new List<double> { time };
        }

        if (current is not null)
        {
            yield return current;
        }
    }

    /// <summary>抽帧到目录：skipNonKey 时只解关键帧（全片几秒钟），否则按 fps 在区间内抽。</summary>
    private async Task<List<double>> ExtractFramesAsync(
        string videoPath,
        string directory,
        double? from,
        double? duration,
        double? fps,
        CancellationToken cancellationToken,
        double? tailSeconds = null)
    {
        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (fps is null)
        {
            startInfo.ArgumentList.Add("-skip_frame");
            startInfo.ArgumentList.Add("nokey");
        }

        if (tailSeconds is not null || from is not null)
        {
            startInfo.ArgumentList.Add("-copyts");
        }

        if (tailSeconds is not null)
        {
            startInfo.ArgumentList.Add("-sseof");
            startInfo.ArgumentList.Add($"-{tailSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        if (from is not null)
        {
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(from.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        if (duration is not null)
        {
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(duration.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        string filters = fps is null
            ? $"{CropFilter},showinfo"
            : $"{CropFilter},fps={fps.Value.ToString(CultureInfo.InvariantCulture)},showinfo";
        foreach (string argument in new[]
        {
            "-v", "info", "-i", videoPath,
            "-vf", filters,
            "-vsync", "0",
            Path.Combine(directory, "f%05d.png"),
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        // showinfo 每输出一帧打一行，行里的 pts_time 就是这一帧的真实时间，
        // 不再靠"关键帧序号 ↔ 另一份时间表"对齐（两者条数可能不一致，会错位）。
        var times = new List<double>();
        foreach (string line in stderr.Split('\n'))
        {
            int index = line.IndexOf("pts_time:", StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            string tail = line[(index + "pts_time:".Length)..].TrimStart();
            int end = 0;
            while (end < tail.Length && (char.IsAsciiDigit(tail[end]) || tail[end] == '.'))
            {
                end++;
            }

            if (end > 0 && double.TryParse(tail[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double time))
            {
                times.Add(time);
            }
        }

        return times;
    }

    private static async Task<(bool Hit, string Text)> RecognizeAsync(
        string imagePath,
        OcrEngine[] engines,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using FileStream stream = File.OpenRead(imagePath);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream()).AsTask(cancellationToken)
            .ConfigureAwait(false);
        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync().AsTask(cancellationToken).ConfigureAwait(false);

        string fallback = "";
        foreach (OcrEngine engine in engines)
        {
            OcrResult result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
            string text = string.Join(" ", result.Lines.Select(line => line.Text));
            if (fallback.Length == 0 && text.Length > 0)
            {
                fallback = text;
            }

            if (IsTrophyText(text))
            {
                return (true, text);
            }
        }

        return (false, fallback);
    }

    /// <summary>PS 横幅的固定文案。OCR 常把「赢」认成 贏/羸/嬴，所以只匹配「得奖杯」这一段。</summary>
    internal static bool IsTrophyText(string text)
    {
        string flat = text.Replace(" ", "", StringComparison.Ordinal);
        return flat.Contains("得奖杯", StringComparison.Ordinal)
               || flat.Contains("得獎盃", StringComparison.Ordinal)
               || flat.Contains("トロフィー", StringComparison.Ordinal)
               || text.Contains("Trophy", StringComparison.OrdinalIgnoreCase);
    }

    private static OcrEngine[] CreateEngines()
    {
        var engines = new List<OcrEngine>();
        foreach (string tag in new[] { "zh-Hans-CN", "ja", "en-US" })
        {
            OcrEngine? engine = OcrEngine.TryCreateFromLanguage(new Language(tag));
            if (engine is not null)
            {
                engines.Add(engine);
            }
        }

        return engines.ToArray();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
