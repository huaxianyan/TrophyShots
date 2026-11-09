namespace TrophyShots.App.ViewModels;

/// <summary>处理进度汇总：多少条已完成、哪些出错、还剩多少没处理。</summary>
public sealed record LibrarySummary(
    int Total,
    int Completed,
    IReadOnlyList<string> Broken,
    IReadOnlyList<string> Pending)
{
    public int NotCompleted => Total - Completed;

    public string Describe() => Pending.Count == 0 && Broken.Count == 0
        ? $"全部处理完：{Completed} / {Total} 条都有截图了。"
        : $"已处理 {Completed} / {Total} 条，出错 {Broken.Count} 条，未处理 {Pending.Count} 条。";
}
