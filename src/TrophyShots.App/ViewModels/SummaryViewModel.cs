using System.Collections.ObjectModel;

namespace TrophyShots.App.ViewModels;

/// <summary>汇总对话框的数据：先报结论，再列需要人看的条目。</summary>
public sealed class SummaryViewModel
{
    public SummaryViewModel(LibrarySummary summary)
    {
        Headline = summary.Pending.Count == 0 && summary.Broken.Count == 0
            ? "全部处理完了"
            : "这一轮到此为止";

        Counts = $"共 {summary.Total} 条 · 已完成 {summary.Completed} 条 · 出错 {summary.Broken.Count} 条 · 未处理 {summary.Pending.Count} 条";

        foreach (string name in summary.Broken)
        {
            Lines.Add(new SummaryLine("出错", name));
        }

        foreach (string name in summary.Pending)
        {
            Lines.Add(new SummaryLine("未处理", name));
        }
    }

    public string Headline { get; }

    public string Counts { get; }

    public ObservableCollection<SummaryLine> Lines { get; } = [];
}

public sealed record SummaryLine(string Kind, string Name);
