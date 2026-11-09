using Xunit;

using TrophyShots.Player;

namespace TrophyShots.Player.Tests;

public sealed class FrameRateAnalysisTests
{
    [Fact]
    public void UniformSixtyFpsIsConstantFrameRate()
    {
        double[] times = Enumerable.Range(0, 120).Select(i => i / 60.0).ToArray();

        Assert.False(MediaProbe.LooksVariableFrameRate(times));
    }

    [Fact]
    public void ReorderedPacketsStillCountAsConstantFrameRate()
    {
        // 真实素材的包顺序是解码顺序，B 帧会让时间戳乱序出现；这里把 1/60 均匀的 12 帧打乱顺序。
        double[] uniform = [.. Enumerable.Range(0, 12).Select(i => i / 60.0)];
        double[] times = [.. uniform.Take(6).Zip(uniform.Skip(6)).SelectMany(pair => new[] { pair.Second, pair.First })];

        Assert.False(MediaProbe.LooksVariableFrameRate(times));
    }

    [Fact]
    public void MillisecondQuantisedSixtyFpsIsStillConstantFrameRate()
    {
        // 时间基 1/1000 时 1/60 会被量化成 0.016 / 0.017 交替出现，这不是变帧率。
        double[] times = [.. Enumerable.Range(0, 300).Select(i => Math.Round(i * 1000 / 60.0) / 1000.0)];

        Assert.False(MediaProbe.LooksVariableFrameRate(times));
    }

    [Fact]
    public void IrregularSpacingIsVariableFrameRate()
    {
        double[] times = [0.017, 0.033, 0.083, 0.100, 0.117, 0.167, 0.183, 0.200, 0.250, 0.267, 0.333, 0.350];

        Assert.True(MediaProbe.LooksVariableFrameRate(times));
    }

    [Fact]
    public void TooFewSamplesCannotBeJudged()
    {
        Assert.False(MediaProbe.LooksVariableFrameRate([0.0, 0.016]));
        Assert.False(MediaProbe.LooksVariableFrameRate([]));
    }

    [Fact]
    public void DuplicateTimestampsDoNotFakeIrregularity()
    {
        double[] times = Enumerable.Range(0, 60).Select(i => i / 30.0).Concat([0.0, 0.0, 0.0]).ToArray();

        Assert.False(MediaProbe.LooksVariableFrameRate(times));
    }
}
