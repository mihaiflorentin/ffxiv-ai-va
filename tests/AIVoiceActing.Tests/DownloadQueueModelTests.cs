namespace AIVoiceActing.Tests;

using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class DownloadQueueModelTests
{
    private static readonly ModelAsset A = new("Alpha", "a.bin", SizeBytes: 1000);
    private static readonly ModelAsset B = new("Beta", "b.bin", SizeBytes: 2000);
    private static readonly ModelAsset C = new("Gamma", "c.bin", SizeBytes: 3000);

    private readonly FakeModelProvisioner provisioner = new();
    private readonly StatusCenter status = new();
    private readonly List<string> info = [];
    private readonly List<string> errors = [];

    private DownloadQueueModel Create() => new(
        () => this.provisioner,
        this.status,
        this.info.Add,
        (message, _) => this.errors.Add(message));

    [Fact]
    public async Task Downloads_RunSequentially_InListOrder()
    {
        var queue = this.Create();
        Assert.True(queue.Start([A, B, C]));

        await this.provisioner.WaitUntilStarted("a.bin");
        Assert.Equal(["a.bin"], this.provisioner.Started);
        Assert.Same(A, queue.Current);
        Assert.Equal(0, queue.FileDone);
        Assert.Equal(3, queue.FileTotal);

        this.provisioner.Release("a.bin");
        await this.provisioner.WaitUntilStarted("b.bin");
        Assert.Equal(["a.bin", "b.bin"], this.provisioner.Started);
        Assert.Same(B, queue.Current);
        Assert.Equal(1, queue.FileDone);

        this.provisioner.Release("b.bin");
        await this.provisioner.WaitUntilStarted("c.bin");
        this.provisioner.Release("c.bin");
        await queue.Completion!;

        Assert.False(queue.Active);
        Assert.Null(queue.Current);
        Assert.Equal(["a.bin", "b.bin", "c.bin"], this.provisioner.Started);
        Assert.Contains(this.status.Active(), m => m.Title == "Downloaded Gamma.");
        Assert.False(this.status.HasError);
    }

    [Fact]
    public async Task Start_WhileActive_IsRejected_AndEmptyListIsIgnored()
    {
        var queue = this.Create();
        Assert.False(queue.Start([]));
        Assert.True(queue.Start([A]));
        Assert.False(queue.Start([B]));

        await this.provisioner.WaitUntilStarted("a.bin");
        this.provisioner.Release("a.bin");
        await queue.Completion!;
        Assert.Equal(["a.bin"], this.provisioner.Started);
    }

    [Fact]
    public async Task Cancel_MidFile_StopsTheQueue_AndPostsAnInfoMessage()
    {
        var queue = this.Create();
        queue.Start([A, B]);
        await this.provisioner.WaitUntilStarted("a.bin");

        queue.Cancel();
        await queue.Completion!;

        Assert.False(queue.Active);
        Assert.Equal(["a.bin"], this.provisioner.Started);
        Assert.Contains(this.status.Active(), m => m.Severity == Severity.Info && m.Title == "Download cancelled.");
        Assert.False(this.status.HasError);
        Assert.Empty(this.errors);
    }

    [Fact]
    public async Task Failure_StopsTheQueue_AndSurfacesAStickyErrorWithRetry()
    {
        this.provisioner.AutoComplete = true;
        this.provisioner.FailOn = "b.bin";
        var queue = this.Create();

        queue.Start([A, B, C]);
        await queue.Completion!;

        Assert.False(queue.Active);
        Assert.Equal(["a.bin", "b.bin"], this.provisioner.Started);
        Assert.Single(this.errors);
        var error = Assert.Single(this.status.Active(), m => m.Severity == Severity.Error);
        Assert.Contains("Beta", error.Title);
        Assert.Contains("disk full", error.Detail);
        Assert.Null(error.ExpiresAt);
        Assert.Equal("Retry", Assert.Single(error.Actions).Label);

        // Retry resumes at the failed file and finishes the rest.
        this.provisioner.FailOn = null;
        Assert.True(this.status.Invoke(error.Id, 0));
        await queue.Completion!;

        Assert.Equal(["a.bin", "b.bin", "b.bin", "c.bin"], this.provisioner.Started);
        Assert.False(this.status.HasError);
    }

    [Fact]
    public async Task Progress_IsReported_AndResetsPerFile()
    {
        this.provisioner.ReportBytes = 400;
        var queue = this.Create();
        queue.Start([A, B]);

        await this.provisioner.WaitUntilStarted("a.bin");
        Assert.Equal(400, queue.Received);
        Assert.Equal(1000, queue.Total);
        Assert.Equal(0.4, queue.Fraction, precision: 3);
        Assert.Contains("Alpha (file 1 of 2)", queue.Caption);

        this.provisioner.Release("a.bin");
        await this.provisioner.WaitUntilStarted("b.bin");
        Assert.Equal(2000, queue.Total);
        Assert.Contains("Beta (file 2 of 2)", queue.Caption);

        this.provisioner.Release("b.bin");
        await queue.Completion!;
        Assert.Equal(0, queue.FileTotal);
        Assert.Equal(0, queue.Received);
    }

    [Fact]
    public async Task Restart_AfterSettling_Works()
    {
        this.provisioner.AutoComplete = true;
        var queue = this.Create();
        queue.Start([A]);
        await queue.Completion!;
        Assert.True(queue.Start([B]));
        await queue.Completion!;
        Assert.Equal(["a.bin", "b.bin"], this.provisioner.Started);
    }
}
