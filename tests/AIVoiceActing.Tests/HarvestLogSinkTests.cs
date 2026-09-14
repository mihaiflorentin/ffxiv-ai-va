namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Dalamud;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class HarvestLogSinkTests : IDisposable
{
    private readonly string filePath = Path.Combine(
        Path.GetTempPath(), "aiva-tests", Guid.NewGuid().ToString("N"), "beast-tribe-harvest.log");

    public void Dispose()
    {
        if (File.Exists(this.filePath))
        {
            File.Delete(this.filePath);
        }

        var directory = Path.GetDirectoryName(this.filePath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConversationLines_AreAppended_AndForwarded()
    {
        var inner = new FakeLogSink();
        var sink = new HarvestLogSink(inner, this.filePath);

        sink.Info("Conversation: name=\"Campingway\" key=npc:campingway race=99 model=1234 world=?");
        sink.Info("Speak requested: something else");

        Assert.Contains("Conversation:", File.ReadAllText(this.filePath));
        Assert.DoesNotContain("Speak requested", File.ReadAllText(this.filePath));
        Assert.Equal(2, inner.Snapshot().Count(c => c.Level == "Info"));
    }

    [Fact]
    public void WarnAndError_ForwardWithoutAppending()
    {
        var inner = new FakeLogSink();
        var sink = new HarvestLogSink(inner, this.filePath);

        sink.Warn("careful");
        sink.Error("boom", new InvalidOperationException());

        Assert.False(File.Exists(this.filePath));
        Assert.Contains(inner.Snapshot(), c => c.Level == "Warn");
        Assert.Contains(inner.Snapshot(), c => c.Level == "Error");
    }

    [Fact]
    public void UnwritablePath_DoesNotThrow()
    {
        // A directory in place of the file: every append fails and must be swallowed.
        Directory.CreateDirectory(this.filePath);
        var sink = new HarvestLogSink(new FakeLogSink(), this.filePath);

        sink.Info("Conversation: name=\"Splittingway\" key=npc:splittingway");
    }
}
