namespace AIVoiceActing.Infrastructure.Audio;

using AIVoiceActing.Ports;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

/// <summary>
/// Windows audio output (port of TextToTalk's StreamSoundQueue playback shape): one
/// shared-mode output per line, 32-bit float mono source scaled by a volume sample
/// provider, blocking until playback completes or <see cref="Cancel"/> stops the device
/// (the port's cancellation channel — the queue's CancelCurrent/Clear route here).
/// Backends are tried in order: WASAPI shared with event sync OFF (polling) — Wine's
/// mmdevapi never raises the event-driven completion callback, so event-synced clients
/// stall silently forever in Proton/XIVLauncher — then winmm waveOut, which Wine routes
/// through winepulse/winealsa. Every attempt is logged. Guarded by
/// <see cref="OperatingSystem.IsWindows"/>: on any other platform the sink reports
/// <c>IsSupported = false</c> and warns once, and the queue skips audio cleanly.
/// </summary>
public sealed class NAudioSink : IAudioSink
{
    private readonly Func<int>? deviceIndexFactory;
    private readonly ILogSink? log;
    private readonly object currentGate = new();
    private bool unsupportedLogged;
    private IWavePlayer? current;

    public NAudioSink(Func<int>? deviceIndexFactory = null, ILogSink? log = null)
    {
        this.deviceIndexFactory = deviceIndexFactory;
        this.log = log;
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Static output-device listing for the configuration UI (empty off-Windows).</summary>
    public static IReadOnlyList<(int Index, string Name)> EnumerateOutputDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return [.. new MMDeviceEnumerator()
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select((device, index) => (index, device.FriendlyName))];
    }

    public void Play(SynthesisResult audio, float volume)
    {
        if (!this.IsSupported)
        {
            this.WarnUnsupportedOnce();
            return;
        }

        var scaled = new VolumeSampleProvider(new MonoFloatSampleProvider(audio.Samples, audio.SampleRate))
        {
            Volume = Math.Clamp(volume, 0f, 2f),
        };

        foreach (var (output, backend) in this.CreateOutputs())
        {
            try
            {
                this.PlayOn(output, backend, scaled, audio);
                return;
            }
            catch (Exception ex)
            {
                this.log?.Warn($"{backend} playback failed: {ex.Message}");
            }
            finally
            {
                output.Dispose();
            }
        }
    }

    /// <summary>Stops current playback immediately (text-advance / voiced-line courtesy).</summary>
    public void Cancel()
    {
        lock (this.currentGate)
        {
            this.current?.Stop();
        }
    }

    /// <summary>Drops every queued item without playing them (nothing buffers here).</summary>
    public void Flush() => this.Cancel();

    private void PlayOn(IWavePlayer output, string backend, ISampleProvider scaled, SynthesisResult audio)
    {
        this.log?.Info($"Playing {audio.Samples.Length / (float)audio.SampleRate:0.0}s via {backend}.");
        using var finished = new ManualResetEventSlim(false);
        output.PlaybackStopped += (_, _) => finished.Set();
        output.Init(scaled);
        output.Play();
        // Register only once playback has begun: a Cancel landing before this point
        // must never be silently no-op'd by the Stop below — the worst case is that
        // it misses the first milliseconds of a just-started line instead.
        lock (this.currentGate)
        {
            this.current = output;
        }

        try
        {
            while (!finished.IsSet)
            {
                finished.Wait(100);
            }

            this.log?.Info("Playback finished.");
        }
        finally
        {
            lock (this.currentGate)
            {
                this.current = null;
            }
        }
    }

    /// <summary>
    /// Output attempts in order: the chosen (or default) WASAPI endpoint in polling mode,
    /// then winmm waveOut. Eagerly constructed so a WASAPI failure at construction falls
    /// through to the next attempt inside <see cref="Play"/>.
    /// </summary>
    private IReadOnlyList<(IWavePlayer Output, string Backend)> CreateOutputs()
    {
        var attempts = new List<(IWavePlayer Output, string Backend)>();
        var devices = new List<MMDevice>();
        try
        {
            var enumerator = new MMDeviceEnumerator();
            devices.AddRange(enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active));
            var index = this.deviceIndexFactory?.Invoke() ?? 0;
            var device = index >= 0 && index < devices.Count
                ? devices[index]
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            // WasapiOut fully consumes the device in its constructor and does not take
            // ownership of the MMDevice, so the endpoints are released below either way.
            attempts.Add((
                new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, 200),
                $"wasapi:{device.FriendlyName}"));
        }
        catch (Exception ex)
        {
            this.log?.Warn($"WASAPI output unavailable ({ex.Message}); will try waveOut.");
        }
        finally
        {
            foreach (var device in devices)
            {
                device.Dispose();
            }
        }

        // Wine runs winmm through winepulse/winealsa — the most reliable last resort.
        attempts.Add((new WaveOutEvent { DesiredLatency = 200 }, "waveOut"));
        return attempts;
    }

    private void WarnUnsupportedOnce()
    {
        if (this.unsupportedLogged)
        {
            return;
        }

        this.unsupportedLogged = true;
        this.log?.Warn("Audio playback is unsupported on this platform (Windows-only WASAPI); speech is skipped.");
    }
}

/// <summary>
/// Synthesis results are mono float PCM; exposes them as a one-shot ISampleProvider at
/// IEEE-float format so WASAPI shared mode resamples/mixes them natively.
/// </summary>
internal sealed class MonoFloatSampleProvider(float[] samples, int sampleRate) : ISampleProvider
{
    private int position;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

    public int Read(float[] buffer, int offset, int count)
    {
        var available = samples.Length - this.position;
        if (available <= 0)
        {
            return 0;
        }

        var toCopy = Math.Min(count, available);
        samples.AsSpan(this.position, toCopy).CopyTo(buffer.AsSpan(offset, toCopy));
        this.position += toCopy;
        return toCopy;
    }
}
