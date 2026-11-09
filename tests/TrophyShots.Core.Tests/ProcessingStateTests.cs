using Xunit;

using TrophyShots.Core;

namespace TrophyShots.Core.Tests;

public sealed class ProcessingStateTests
{
    [Fact]
    public void MissingStateFileLoadsEmptyState()
    {
        string path = Path.Combine(Path.GetTempPath(), "trophyshots", Guid.NewGuid().ToString("N"), ".state.json");

        ProcessingState state = ProcessingState.Load(path);

        Assert.Null(state.LastFileName);
        Assert.Equal(0, state.LastPositionSeconds);
    }

    [Fact]
    public void SavedStateSurvivesRoundTrip()
    {
        string directory = Directory.CreateTempSubdirectory("trophyshots-state-").FullName;
        try
        {
            string path = VideoLibrary.StatePathFor(directory);
            new ProcessingState { LastFileName = "Replay 2026-10-22 00-01-42.mkv", LastPositionSeconds = 128.5 }
                .Save(path);

            ProcessingState loaded = ProcessingState.Load(path);

            Assert.Equal("Replay 2026-10-22 00-01-42.mkv", loaded.LastFileName);
            Assert.Equal(128.5, loaded.LastPositionSeconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CorruptStateFileSelfHealsToEmptyState()
    {
        string directory = Directory.CreateTempSubdirectory("trophyshots-state-").FullName;
        try
        {
            string path = VideoLibrary.StatePathFor(directory);
            File.WriteAllText(path, "{ 这不是 JSON");

            ProcessingState state = ProcessingState.Load(path);

            Assert.Null(state.LastFileName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
