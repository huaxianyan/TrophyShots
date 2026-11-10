using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TrophyShots.Player;

namespace TrophyShots.App.ViewModels;

/// <summary>列表里的一条录像。时长由 ffprobe 后台补上，完成状态以截图是否存在为准。</summary>
public partial class VideoItemViewModel : ObservableObject
{
    public VideoItemViewModel(string fullPath, bool completed)
    {
        FullPath = fullPath;
        _completed = completed;
    }

    public string FullPath { get; }

    public string FileName => Path.GetFileName(FullPath);

    /// <summary>ffprobe 读到的时长；读失败时为空。</summary>
    public TimeSpan? Duration { get; private set; }

    /// <summary>帧率不是常数（r_frame_rate ≠ avg_frame_rate），逐帧步进要退化成按时间步进。</summary>
    public bool IsVariableFrameRate { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _completed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private string? _detail = "读取时长…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _broken;

    /// <summary>预处理得到的奖杯片段（按时间正序）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrophies))]
    private IReadOnlyList<TrophySegment> _segments = Array.Empty<TrophySegment>();

    /// <summary>预处理状态：未检测 / 检测中… / 奖杯 ×N / 未检出 / 检测失败。</summary>
    [ObservableProperty]
    private string _trophyText = "未检测";

    public bool HasTrophies => Segments.Count > 0;

    public void MarkTrophyPending() => TrophyText = "未检测";

    public void MarkTrophyRunning() => TrophyText = "检测中…";

    public void MarkTrophyFailed() => TrophyText = "检测失败";

    public void ApplyTrophies(IReadOnlyList<TrophySegment> segments)
    {
        Segments = segments;
        TrophyText = segments.Count == 0
            ? "未检出"
            : $"奖杯 {TimeSpan.FromSeconds(segments[^1].StartSeconds):mm\\:ss}";
    }

    /// <summary>列表里的小预览图，后台生成，可能为空。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    public string DetailText => Broken ? $"{DurationText} · 无法读取视频流" : DurationText;

    public string StatusText => Broken ? "出错" : Completed ? "已完成" : "未处理";

    private string DurationText { get; set; } = "";

    public void ApplyProbe(MediaProbeResult result)
    {
        if (result.Duration is null)
        {
            Broken = true;
            Detail = "读取时长失败";
            return;
        }

        Duration = result.Duration;
        IsVariableFrameRate = result.IsVariableFrameRate;
        string duration = FormatDuration(result.Duration.Value);
        DurationText = result.IsVariableFrameRate ? $"{duration} · 非常数帧率" : duration;
        Detail = DurationText;
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
}
