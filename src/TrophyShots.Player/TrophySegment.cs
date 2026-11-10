namespace TrophyShots.Player;

/// <summary>录像里的一段"跳奖杯"：横幅出现到消失。</summary>
public sealed record TrophySegment(double StartSeconds, double EndSeconds, string Text)
{
    public double LengthSeconds => EndSeconds - StartSeconds;

    /// <summary>跳转落点：提前一秒，方便看到弹出前的画面。</summary>
    public double JumpSeconds => Math.Max(0, StartSeconds - 1.0);
}
