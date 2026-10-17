namespace AIVoiceActing.Tests.Mock;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using Microsoft.ML.OnnxRuntime;

/// <summary>Fake WebGPU runtime: scripted listing, records plans it was asked to attach.</summary>
public sealed class FakeWebGpuRuntime : IWebGpuRuntime
{
    public WebGpuEnumeration Listing { get; set; } = new(
        true,
        [new DeviceAdapter("AMD", "Radeon RX 9070 XT", "Vulkan")],
        string.Empty);

    public Exception? AppendThrows { get; set; }

    public int EnumerateCalls { get; private set; }

    public List<DevicePlan> Appended { get; } = [];

    public WebGpuEnumeration Enumerate()
    {
        this.EnumerateCalls++;
        return this.Listing;
    }

    public void Append(SessionOptions options, DevicePlan plan)
    {
        if (this.AppendThrows is { } ex)
        {
            throw ex;
        }

        this.Appended.Add(plan);
    }
}

/// <summary>Fake IDeviceProbe (census fake pattern): returns a canned report and counts Detect calls.</summary>
public sealed class FakeDeviceProbe : IDeviceProbe
{
    public DeviceReport? LastReport { get; set; }

    public DeviceReport Next { get; set; } = DeviceReport.Unknown;

    public int DetectCalls { get; private set; }

    public DeviceReport Detect()
    {
        this.DetectCalls++;
        return this.LastReport = this.Next;
    }
}

/// <summary>Fake planner: returns scripted plans, records the arguments, can block to observe the Loading state.</summary>
public sealed class FakeDevicePlanner : IDevicePlanner
{
    public DevicePreferences Preferences { get; set; } = DevicePreferences.Default;

    public DeviceReport? LastReport { get; set; }

    public DevicePlan Plan { get; set; } = DevicePlan.OnCpu("Running on the CPU, as you chose.");

    public DevicePlan? CheapPlan { get; set; }

    public List<(string Lm, bool Cheap, DeviceMode? Override)> Calls { get; } = [];

    /// <summary>When set, full (non-cheap) plans wait here until released.</summary>
    public ManualResetEventSlim? Gate { get; set; }

    public ManualResetEventSlim Entered { get; } = new(false);

    public DevicePlan PlanFor(string engineId, string modelsDir, string lmGraphFile, bool cheap, DeviceMode? modeOverride = null)
    {
        lock (this.Calls)
        {
            this.Calls.Add((lmGraphFile, cheap, modeOverride));
        }

        if (!cheap && this.Gate is { } gate)
        {
            this.Entered.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
        }

        return cheap && this.CheapPlan is { } preview ? preview : this.Plan;
    }
}
