namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class GpuCrashGuardTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "aiva-guard", Guid.NewGuid().ToString("N"));

    private string Marker => Path.Combine(this.dir, "gpu-session.pending");

    public void Dispose()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }

    [Fact]
    public void Begin_WritesTheMarker_AndSucceededRemovesIt()
    {
        var guard = new GpuCrashGuard(this.Marker);

        Assert.True(guard.TryBegin("lm session", out var scope));
        Assert.True(File.Exists(this.Marker));
        Assert.Contains("lm session", File.ReadAllText(this.Marker));

        scope.Succeeded();

        Assert.False(File.Exists(this.Marker));
    }

    [Fact]
    public void FailedSafely_AlsoRemovesTheMarker()
    {
        var guard = new GpuCrashGuard(this.Marker);
        guard.TryBegin("x", out var scope);

        scope.FailedSafely();

        Assert.False(File.Exists(this.Marker));
    }

    [Fact]
    public void ACompletedScope_CompletesOnlyOnce()
    {
        var guard = new GpuCrashGuard(this.Marker);
        guard.TryBegin("a", out var first);
        guard.TryBegin("b", out var second);

        first.Succeeded();
        first.Succeeded(); // must not release the second scope's hold

        Assert.True(File.Exists(this.Marker));
        second.Succeeded();
        Assert.False(File.Exists(this.Marker));
    }

    [Fact]
    public void OverlappingScopes_KeepTheMarkerUntilTheLastCompletes()
    {
        var guard = new GpuCrashGuard(this.Marker);
        guard.TryBegin("session", out var session);
        guard.TryBegin("first run", out var run);

        session.Succeeded();
        Assert.True(File.Exists(this.Marker));

        run.Succeeded();
        Assert.False(File.Exists(this.Marker));
    }

    [Fact]
    public void ALeftoverMarker_IsReportedAtStartup_WithItsDetail()
    {
        Directory.CreateDirectory(this.dir);
        File.WriteAllText(this.Marker, "2026-10-17 pid 1: WebGpu session");

        var guard = new GpuCrashGuard(this.Marker);

        Assert.True(guard.HasLeftoverMarker(out var detail));
        Assert.Contains("WebGpu session", detail);
    }

    [Fact]
    public void NoMarker_NoLeftover()
    {
        var guard = new GpuCrashGuard(this.Marker);

        Assert.False(guard.HasLeftoverMarker(out _));
    }

    [Fact]
    public void AMarkerWrittenByThisProcess_IsNotALeftover()
    {
        var guard = new GpuCrashGuard(this.Marker);
        guard.TryBegin("in flight", out _);

        Assert.False(guard.HasLeftoverMarker(out _));
    }

    [Fact]
    public void TripIfLeftover_PersistsTheFlagBeforeClearingTheMarker()
    {
        Directory.CreateDirectory(this.dir);
        File.WriteAllText(this.Marker, "crashed");
        var guard = new GpuCrashGuard(this.Marker);
        var markerSeenByCallback = false;

        var tripped = guard.TripIfLeftover(_ => markerSeenByCallback = File.Exists(this.Marker));

        Assert.True(tripped);
        Assert.True(markerSeenByCallback);
        Assert.False(File.Exists(this.Marker));
    }

    [Fact]
    public void TripIfLeftover_KeepsTheMarkerWhenPersistingFails()
    {
        Directory.CreateDirectory(this.dir);
        File.WriteAllText(this.Marker, "crashed");
        var guard = new GpuCrashGuard(this.Marker);

        Assert.Throws<IOException>(() => guard.TripIfLeftover(_ => throw new IOException("disk full")));

        Assert.True(File.Exists(this.Marker));
    }

    [Fact]
    public void TripIfLeftover_DoesNothingWithoutAMarker()
    {
        var guard = new GpuCrashGuard(this.Marker);
        var called = false;

        Assert.False(guard.TripIfLeftover(_ => called = true));
        Assert.False(called);
    }

    [Fact]
    public void AnUnwritableMarkerPath_RefusesTheGpu()
    {
        // A file where the directory should be: CreateDirectory fails.
        Directory.CreateDirectory(this.dir);
        var blocker = Path.Combine(this.dir, "blocker");
        File.WriteAllText(blocker, "x");
        var log = new FakeLogSink();
        var guard = new GpuCrashGuard(Path.Combine(blocker, "gpu-session.pending"), log);

        Assert.False(guard.TryBegin("lm", out _));
        Assert.Contains(log.Snapshot(), e => e.Level == "Warn");
    }
}
