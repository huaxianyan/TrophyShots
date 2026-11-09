using Xunit;

using TrophyShots.Core;

namespace TrophyShots.Core.Tests;

public sealed class ResumePolicyTests
{
    private const string File = "Replay 2026-10-22 00-01-37.mkv";

    [Fact]
    public void ResumesWhenStatePointsAtSameVideo()
    {
        var state = new ProcessingState { LastFileName = File, LastPositionSeconds = 128.5 };

        Assert.Equal(128.5, ResumePolicy.Resolve(state, File, durationSeconds: 300));
    }

    [Fact]
    public void StartsFromBeginningForAnotherVideo()
    {
        var state = new ProcessingState { LastFileName = "别的录像.mkv", LastPositionSeconds = 128.5 };

        Assert.Equal(0, ResumePolicy.Resolve(state, File, durationSeconds: 300));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-3d)]
    [InlineData(0.01d)]
    public void StartsFromBeginningWhenPositionIsMissingOrNegligible(double? position)
    {
        var state = new ProcessingState { LastFileName = File, LastPositionSeconds = position ?? 0 };

        Assert.Equal(0, ResumePolicy.Resolve(state, File, durationSeconds: 300));
    }

    [Fact]
    public void StartsFromBeginningWhenPositionIsAtTheTail()
    {
        var state = new ProcessingState { LastFileName = File, LastPositionSeconds = 299.6 };

        Assert.Equal(0, ResumePolicy.Resolve(state, File, durationSeconds: 300));
    }

    [Fact]
    public void StartsFromBeginningWithoutState()
    {
        Assert.Equal(0, ResumePolicy.Resolve(null, File, durationSeconds: 300));
    }
}
