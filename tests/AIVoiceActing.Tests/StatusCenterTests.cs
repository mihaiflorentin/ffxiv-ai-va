namespace AIVoiceActing.Tests;

using AIVoiceActing.UI.State;
using Xunit;

public sealed class StatusCenterTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan span) => this.Now += span;
    }

    private static (StatusCenter Center, Clock Clock) Make()
    {
        var clock = new Clock();
        return (new StatusCenter(() => clock.Now), clock);
    }

    [Fact]
    public void Post_ReturnsActiveMessageWithFields()
    {
        var (c, _) = Make();
        var m = c.Post(Severity.Info, "Saved", "Voice list updated");
        var active = c.Active();
        Assert.Single(active);
        Assert.Equal(m.Id, active[0].Id);
        Assert.Equal("Saved", active[0].Title);
        Assert.Equal("Voice list updated", active[0].Detail);
        Assert.Equal(Severity.Info, active[0].Severity);
    }

    [Fact]
    public void Toasts_ExpireAfterTheirLifetime()
    {
        var (c, clock) = Make();
        c.Post(Severity.Success, "Downloaded Kokoro");
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Single(c.Active());
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(c.Active());
    }

    [Fact]
    public void Errors_AreStickyUntilDismissed()
    {
        var (c, clock) = Make();
        var m = c.Post(Severity.Error, "Download failed", "No network");
        clock.Advance(TimeSpan.FromHours(5));
        Assert.Single(c.Active());
        c.Dismiss(m.Id);
        Assert.Empty(c.Active());
    }

    [Fact]
    public void Resolve_ClearsByKey()
    {
        var (c, _) = Make();
        c.Post(Severity.Error, "Engine failed", key: "engine");
        c.Post(Severity.Info, "Other");
        c.Resolve("engine");
        Assert.Single(c.Active());
        Assert.Equal("Other", c.Active()[0].Title);
    }

    [Fact]
    public void DuplicatePosts_AreCollapsedAndCounted()
    {
        var (c, clock) = Make();
        var first = c.Post(Severity.Warning, "Using CPU", "GPU not found");
        clock.Advance(TimeSpan.FromSeconds(4));
        var second = c.Post(Severity.Warning, "Using CPU", "GPU not found");
        Assert.Equal(first.Id, second.Id);
        var only = Assert.Single(c.Active());
        Assert.Equal(2, only.Count);

        // The repeat refreshed the timer: still alive 6s after the first post.
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Single(c.Active());
    }

    [Fact]
    public void DifferentDetail_IsNotADuplicate()
    {
        var (c, _) = Make();
        c.Post(Severity.Error, "Download failed", "a.onnx");
        c.Post(Severity.Error, "Download failed", "b.onnx");
        Assert.Equal(2, c.Active().Count);
    }

    [Fact]
    public void ExplicitKey_DeduplicatesRegardlessOfText_AndKeepsNewestText()
    {
        var (c, _) = Make();
        c.Post(Severity.Error, "Download failed 10%", key: "dl");
        c.Post(Severity.Error, "Download failed 20%", key: "dl");
        var only = Assert.Single(c.Active());
        Assert.Equal("Download failed 20%", only.Title);
        Assert.Equal(2, only.Count);
    }

    [Fact]
    public void Active_ListsErrorsFirstThenNewest()
    {
        var (c, clock) = Make();
        c.Post(Severity.Info, "old info");
        clock.Advance(TimeSpan.FromSeconds(1));
        c.Post(Severity.Error, "boom");
        clock.Advance(TimeSpan.FromSeconds(1));
        c.Post(Severity.Info, "new info");
        Assert.Equal(new[] { "boom", "new info", "old info" }, c.Active().Select(m => m.Title).ToArray());
    }

    [Fact]
    public void InvokeAction_RunsItAndDismissesTheMessage()
    {
        var (c, _) = Make();
        var calls = 0;
        var m = c.Post(Severity.Error, "Failed", actions: new[] { new StatusAction("Retry", () => calls++) });
        Assert.True(c.Invoke(m.Id, 0));
        Assert.Equal(1, calls);
        Assert.Empty(c.Active());
        Assert.False(c.Invoke(m.Id, 0));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Invoke_RejectsBadIndex()
    {
        var (c, _) = Make();
        var m = c.Post(Severity.Info, "x");
        Assert.False(c.Invoke(m.Id, 3));
        Assert.Single(c.Active());
    }

    [Fact]
    public void PostUndo_OffersUndoForEightSecondsOnce()
    {
        var (c, clock) = Make();
        var undone = 0;
        var m = c.PostUndo("Removed Alisaie's override", () => undone++);
        Assert.Equal("Undo", m.Actions[0].Label);
        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.Single(c.Active());
        Assert.True(c.Invoke(m.Id, 0));
        Assert.Equal(1, undone);
        Assert.Empty(c.Active());
    }

    [Fact]
    public void PostUndo_ExpiresAfterEightSeconds()
    {
        var (c, clock) = Make();
        c.PostUndo("Removed", () => { });
        clock.Advance(TimeSpan.FromSeconds(8.5));
        Assert.Empty(c.Active());
    }

    [Fact]
    public void HasError_ReflectsStickyErrors()
    {
        var (c, _) = Make();
        Assert.False(c.HasError);
        var m = c.Post(Severity.Error, "bad");
        Assert.True(c.HasError);
        c.Dismiss(m.Id);
        Assert.False(c.HasError);
    }

    [Fact]
    public void Post_FromManyThreads_IsSafe()
    {
        var (c, _) = Make();
        Parallel.For(0, 200, i => c.Post(Severity.Error, "e" + (i % 10)));
        Assert.Equal(10, c.Active().Count);
    }
}
