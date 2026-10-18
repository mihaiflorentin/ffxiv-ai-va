namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;

/// <summary>
/// The things the Processor card and its fallback banner do: pick where voices run, retry on the
/// graphics card, and stop trying it. Settings are written and saved here; the port's reason text is
/// never rewritten.
/// </summary>
internal sealed class ProcessorActions(UiContext ctx)
{
    private Task? retry;

    /// <summary>A retry on the graphics card is running.</summary>
    public bool Retrying => this.retry is { IsCompleted: false };

    /// <summary>Writes the Processor choice (<see cref="DeviceSettings.ModeAuto"/>, gpu or cpu) and rebuilds the voices.</summary>
    public void SetMode(string mode)
    {
        if (ctx.Config.Device.Mode == mode)
        {
            return;
        }

        ctx.Config.Device.Mode = mode;
        this.Apply();
    }

    public void SetPreference(string preference)
    {
        if (ctx.Config.Device.GpuPreference == preference)
        {
            return;
        }

        ctx.Config.Device.GpuPreference = preference;
        this.Apply();
    }

    /// <summary>"Use CPU": stop trying the graphics card.</summary>
    public void UseCpu()
    {
        ctx.Config.Device.Mode = DeviceSettings.ModeCpu;
        this.Apply();
        ctx.Report("Voices will use your processor.", isError: false);
    }

    /// <summary>
    /// "Retry on GPU": rebuilds the voices on the graphics card (which also clears the crash guard).
    /// <paramref name="target"/> is the engine that owns the graphics-card session; null retries the main engine.
    /// </summary>
    public void RetryOnGpu(ISpeechSynthesizer? target)
    {
        if (this.Retrying)
        {
            return;
        }

        var synth = target ?? ctx.Synth;
        ctx.Status.Resolve(EngineSessionModel.FailureKey);
        this.retry = Task.Run(async () =>
        {
            try
            {
                await synth.RetryAsync(DeviceMode.Gpu);

                // The engine clears the crash flag itself; this covers engines that have no device choice.
                if (ctx.Config.Device.GpuDisabledByCrashGuard)
                {
                    ctx.Config.Device.GpuDisabledByCrashGuard = false;
                    ctx.Save();
                }
            }
            catch (Exception ex)
            {
                ctx.LogError("Retry on the graphics card failed.", ex);
                ctx.Report($"Could not retry on the graphics card: {ex.Message}", isError: true);
            }
        });
    }

    private void Apply()
    {
        ctx.Save();
        ctx.InvalidateSynthesizer();

        // Load the new setup now, so the status above answers right away instead of on the next spoken line.
        if (EngineParts.MissingRequired(ctx).Count == 0)
        {
            ctx.EngineSession.StartWarmUp(ctx.PrimaryKey, ctx.Synth);
        }
    }
}
