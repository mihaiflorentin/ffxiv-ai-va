namespace AIVoiceActing.Tests;

using AIVoiceActing.Container;
using AIVoiceActing.Infrastructure.Kokoro;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class ServiceContainerTests
{
    [Fact]
    public void LogSink_OverrideIsCached()
    {
        var sink = new FakeLogSink();
        using var container = new ServiceContainer(logSinkOverride: sink);

        ILogSink first = container.LogSink;
        ILogSink second = container.LogSink;

        Assert.Same(sink, first);
        Assert.Same(first, second);
    }

    [Fact]
    public void LogSink_FactoryIsInvokedExactlyOnce()
    {
        using var container = new ServiceContainer(logSinkFactory: () => new FakeLogSink());

        ILogSink first = container.LogSink;
        ILogSink second = container.LogSink;

        Assert.Same(first, second);
    }

    [Fact]
    public void LogSink_OverrideWinsOverFactory()
    {
        var sink = new FakeLogSink();
        using var container = new ServiceContainer(
            logSinkOverride: sink,
            logSinkFactory: () => throw new InvalidOperationException("factory must not be used"));

        Assert.Same(sink, container.LogSink);
    }

    [Fact]
    public void LogSink_CallsThroughToSink()
    {
        var sink = new FakeLogSink();
        using var container = new ServiceContainer(logSinkOverride: sink);

        container.LogSink.Info("hello");
        container.LogSink.Warn("careful");
        container.LogSink.Error("boom", new Exception("inner"));

        var calls = sink.Snapshot();
        Assert.Equal(3, calls.Count);
        Assert.Equal(("Info", "hello", null), (calls[0].Level, calls[0].Message, calls[0].Exception));
        Assert.Equal(("Warn", "careful", null), (calls[1].Level, calls[1].Message, calls[1].Exception));
        Assert.Equal("Error", calls[2].Level);
        Assert.Equal("inner", calls[2].Exception?.Message);
    }

    [Fact]
    public void LogSink_ThrowsWithoutConfiguration()
    {
        using var container = new ServiceContainer();

        Assert.Throws<InvalidOperationException>(() => _ = container.LogSink);
    }
}

public static class ServiceContainerTestDirs
{
    public static string Create() =>
        Path.Combine(Path.GetTempPath(), "aiva-tests-" + Guid.NewGuid().ToString("N"));
}

public sealed class ServiceContainerInvalidationTests
{
    [Fact]
    public async Task Invalidate_SwapsInstances_AndDisposesRetiredEngineInBackground()
    {
        var modelsDir = ServiceContainerTestDirs.Create();
        Directory.CreateDirectory(modelsDir);
        try
        {
            using var container = new ServiceContainer(
                logSinkOverride: new FakeLogSink(),
                modelsDirFactory: () => modelsDir,
                selectedEngineFactory: () => "kokoro");

            var first = container.SpeechSynthesizer;
            container.InvalidateSpeechSynthesizer();
            var second = container.SpeechSynthesizer;

            Assert.NotSame(first, second);

            // Dispose is now asynchronous (bounded drain on the thread pool): poll.
            var retired = Assert.IsType<KokoroSynthesizer>(first);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!retired.IsDisposed && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.True(retired.IsDisposed);
            Assert.False(((KokoroSynthesizer)second).IsDisposed);
        }
        finally
        {
            Directory.Delete(modelsDir, recursive: true);
        }
    }
}
