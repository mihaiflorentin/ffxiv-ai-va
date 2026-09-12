namespace AIVoiceActing.Infrastructure.Audio;

using AIVoiceActing.Ports;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

/// <summary>
/// Windows audio output (port of TextToTalk's StreamSoundQueue playback shape): one shared
/// -mode WASAPI output per line, 32-bit float mono source scaled by a volume sample
/// provider, blocking until playback completes or <see cref="Cancel"/> stops the device
/// (the port's cancellation channel — the queue's CancelCurrent/Clear route here).
/// Guarded by <see cref="OperatingSystem.IsWindows"/>: on any other platform the sink
/// reports <c>IsSupported = false</c> and warns once, and the queue skips audio cleanly.
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

        var source = new MonoFloatSampleProvider(audio.Samples, audio.SampleRate);
        var scaled = new VolumeSampleProvider(source) { Volume = Math.Clamp(volume, 0f, 2f) };
        var output = this.CreateOutput();
        using var finished = new ManualResetEventSlim(false);
        output.PlaybackStopped += (_, _) => finished.Set();

        lock (this.currentGate)
        {
            this.current = output;
        }

        try
        {
            output.Init(scaled);
            output.Play();
            while (!finished.IsSet)
            {
                finished.Wait(100);
            }
        }
        finally
        {
            lock (this.currentGate)
            {
                this.current = null;
            }

            output.Dispose();
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

    private IWavePlayer CreateOutput()
    {
        var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToArray();
        var index = this.deviceIndexFactory?.Invoke() ?? 0;
        if (index >= 0 && index < devices.Length)
        {
            return new WasapiOut(devices[index], AudioClientShareMode.Shared, false, 200);
        }

        return new WasapiOut(AudioClientShareMode.Shared, 200);
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
        var toCopy = Math.Min(count, available);
        if (toCopy <= 0)
        {
            return 0;
        }

        Array.Copy(samples, this.position, buffer, offset, toCopy);
        this.position += toCopy;
        return toCopy;
    }
}
