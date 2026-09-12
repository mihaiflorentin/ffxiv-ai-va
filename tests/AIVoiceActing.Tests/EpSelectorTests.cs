namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Onnx;
using Xunit;

public sealed class EpSelectorTests : IDisposable
{
    [Fact]
    public void Candidates_MacOsDefaultsToCpuWithCoremlSelectable()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return; // platform-gated: the plugin asserts the right list per OS in-game
        }

        var candidates = EpSelector.CandidateEps();
        Assert.Equal("cpu", candidates[0]); // gate finding: CoreML stalls on the LM at run time
        Assert.Contains("coreml", candidates);
    }
    [Fact]
    public void Candidates_WindowsStartsWithDirectml()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var candidates = EpSelector.CandidateEps();
        Assert.Equal("directml", candidates[0]);
    }

    [Fact]
    public void Cpu_AlwaysCreatesSessionOptions()
    {
        var (options, ep) = EpSelector.CreateSessionOptions("cpu");
        Assert.Equal("cpu", ep);
        options.Dispose();
    }

    [Fact]
    public void Coreml_OnMacOS_AppendsOrFallsBackCleanly()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var logs = new List<string>();
        var (options, ep) = EpSelector.CreateSessionOptions("coreml", logs.Add);
        Assert.Contains(ep, (string[])["coreml", "cpu"]);
        if (ep == "cpu")
        {
            Assert.Contains(logs, l => l.Contains("CoreML", StringComparison.Ordinal));
        }

        options.Dispose();
    }

    [Fact]
    public void Directml_OffWindows_FallsBackToCpuWithLog()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // real DML wiring is the plugin's; core builds always fall back
        }

        var logs = new List<string>();
        var (options, ep) = EpSelector.CreateSessionOptions("directml", logs.Add);
        Assert.Equal("cpu", ep);
        Assert.Contains(logs, l => l.Contains("DirectML", StringComparison.Ordinal));
        options.Dispose();
    }

    [Fact]
    public void Unknown_Throws()
    {
        Assert.Throws<ArgumentException>(() => EpSelector.CreateSessionOptions("cuda"));
    }

    public void Dispose() => GC.Collect();
}
