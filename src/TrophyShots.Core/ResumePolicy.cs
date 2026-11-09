namespace TrophyShots.Core;

/// <summary>
/// 断点续做的判定：只有"上次停在的就是这条录像、位置还在片内"才跳到上次的位置，
/// 否则从头开始（避免换片时莫名其妙跳到中间，也避免停在片尾又续不上）。
/// </summary>
public static class ResumePolicy
{
    public const double MinimumPositionSeconds = 0.05;
    public const double TailMarginSeconds = 1.0;

    public static double Resolve(ProcessingState? state, string fileName, double durationSeconds)
    {
        if (state?.LastFileName is null
            || !string.Equals(state.LastFileName, fileName, StringComparison.Ordinal))
        {
            return 0;
        }

        double position = state.LastPositionSeconds;
        if (!double.IsFinite(position) || position < MinimumPositionSeconds)
        {
            return 0;
        }

        if (durationSeconds > TailMarginSeconds && position > durationSeconds - TailMarginSeconds)
        {
            return 0;
        }

        return position;
    }
}
