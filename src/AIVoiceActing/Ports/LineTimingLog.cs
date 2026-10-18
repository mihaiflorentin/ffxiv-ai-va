namespace AIVoiceActing.Ports;

/// <summary>
/// Where one spoken line spent its time, in milliseconds. Carries no line text, speaker or voice name, so it
/// can go into a bug report as is. Lines that span several chunks add their stages up; a stage that was not
/// measured (or did not happen, such as an encoder run on a cache hit) is 0, and the moments that can be
/// absent (<see cref="FirstAudioMs"/>, <see cref="DeviceOpenMs"/>, <see cref="StartToSoundMs"/>) are -1.
/// </summary>
/// <param name="Id">Process-wide line number, newest highest; a line recorded again (playback finished) replaces its earlier entry.</param>
/// <param name="QueueWaitMs">From arrival to the start of the work: the line before it still rendering.</param>
/// <param name="ProfileMs">Finding or creating the speaker's voice.</param>
/// <param name="PlanMs">Planning the delivery (the emotion reader when it is on).</param>
/// <param name="ReferenceKind">Where the voice clip came from: "user clip", "pack clip", "pack clip pitched", "cached", "rendered"; empty when the engine needs none.</param>
/// <param name="ReferencePrepMs">Getting that clip ready (a rendered one costs seconds).</param>
/// <param name="EncoderMs">Reading the clip into the voice model; 0 when it was remembered.</param>
/// <param name="GateWaitMs">Waiting for the engine to finish the previous render.</param>
/// <param name="PrefillMs">The language model's first step (clip and text), apart from the per-word steps.</param>
/// <param name="LmTokens">Speech tokens the language model produced.</param>
/// <param name="LmMs">Time the language model ran.</param>
/// <param name="LmMsPerTokenP50">Median time of one language model step after the first; a graphics card shared with the game shows up here.</param>
/// <param name="LmMsPerTokenP95">95th percentile of the same.</param>
/// <param name="DecoderMsTotal">Time inside the voice decoder.</param>
/// <param name="Windows">Decoder calls made.</param>
/// <param name="FirstAudioMs">From arrival to the first sound being handed to playback.</param>
/// <param name="DeviceOpenMs">Opening the sound device, per chunk added up.</param>
/// <param name="StartToSoundMs">From arrival to the sound device being fed the first sound.</param>
/// <param name="UnderrunCount">Times playback ran dry and played silence while the line was still being made.</param>
/// <param name="SilenceMs">Total silence played that way.</param>
/// <param name="TotalRenderMs">From the start of the work to the last sound being made.</param>
/// <param name="AudioMs">Length of the finished speech.</param>
/// <param name="Engine">Engine and where it ran, for example "turbo gpu q4f16".</param>
/// <param name="Streamed">Playback started before the line was complete.</param>
public sealed record LineTiming(
    long Id,
    int LineChars,
    int Chunks,
    long QueueWaitMs,
    long ProfileMs,
    long PlanMs,
    string ReferenceKind,
    long ReferencePrepMs,
    long EncoderMs,
    long GateWaitMs,
    long PrefillMs,
    int LmTokens,
    long LmMs,
    long LmMsPerTokenP50,
    long LmMsPerTokenP95,
    long DecoderMsTotal,
    int Windows,
    long FirstAudioMs,
    long DeviceOpenMs,
    long StartToSoundMs,
    int UnderrunCount,
    long SilenceMs,
    long TotalRenderMs,
    long AudioMs,
    string Engine,
    bool Streamed);

/// <summary>Driven port keeping the stage timings of the last few lines, for the diagnostics report.</summary>
public interface ILineTimingLog
{
    /// <summary>Stores <paramref name="timing"/>; an entry with the same <see cref="LineTiming.Id"/> is replaced in place, and the oldest entry goes when the log is full.</summary>
    void Record(LineTiming timing);

    /// <summary>The most recent lines, newest first, at most <paramref name="max"/>.</summary>
    IReadOnlyList<LineTiming> Recent(int max);
}
