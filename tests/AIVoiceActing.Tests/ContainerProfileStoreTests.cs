namespace AIVoiceActing.Tests;

using AIVoiceActing.Container;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class ContainerProfileStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "aiva-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    [Fact]
    public void ProfileStore_IsLazyCached_AndBackedByConfiguredPath()
    {
        using var container = new ServiceContainer(
            logSinkOverride: new FakeLogSink(),
            profileStorePathFactory: () => Path.Combine(this.directory, "voice-assignments.json"));

        var first = container.ProfileStore;
        var second = container.ProfileStore;
        Assert.Same(first, second);

        var profile = first.GetOrCreate("npc:feo ul", () => ["default"], race: null, tribe: null, sex: null);
        Assert.Equal("default", profile.ReferenceVoiceId);
        Assert.True(File.Exists(Path.Combine(this.directory, "voice-assignments.json")));
    }

    [Fact]
    public void ProfileStore_ThrowsWithoutPathFactory()
    {
        using var container = new ServiceContainer(logSinkOverride: new FakeLogSink());
        Assert.Throws<InvalidOperationException>(() => _ = container.ProfileStore);
    }

    [Fact]
    public void ProfileStore_PathFactoryIsEvaluatedLazily()
    {
        using var container = new ServiceContainer(
            logSinkOverride: new FakeLogSink(),
            profileStorePathFactory: () => throw new InvalidOperationException("premature path probe"));

        _ = container.LogSink; // other services must not touch the store path
        Assert.Throws<InvalidOperationException>(() => _ = container.ProfileStore);
    }
}
