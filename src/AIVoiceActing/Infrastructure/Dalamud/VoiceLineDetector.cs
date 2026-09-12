namespace AIVoiceActing.Infrastructure.Dalamud;

using System.Runtime.InteropServices;
using AIVoiceActing.Domain.Pipeline;
using AIVoiceActing.Ports;
using global::Dalamud.Hooking;
using global::Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

/// <summary>
/// Voiced-line courtesy hooks (port of SoundHandler): signature hooks on the game's sound
/// loader/player track which loaded .scd containers are the game's own voice lines, and
/// raise <see cref="IVoiceLineDetector.VoiceLinePlayback"/> when one plays — so synthesized
/// speech yields to the real voice acting. Path classification is the pure
/// <see cref="VoiceLinePathMatcher"/>.
/// </summary>
public sealed unsafe class VoiceLineDetector : IVoiceLineDetector
{
    // Signature strings drawn from Anna Clemens's Sound Filter plugin (via TextToTalk).
    private const string LoadSoundFileSignature = "E8 ?? ?? ?? ?? 48 85 C0 75 12 B0 F6";

    private const string PlaySpecificSoundSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 33 F6 8B DA 48 8B F9 0F BA E2 0F";

    private delegate nint LoadSoundFileDelegate(nint resourceHandlePtr, uint arg2);

    private delegate nint PlaySpecificSoundDelegate(nint soundPtr, int arg2);

    private static readonly int ResourceDataOffset = Marshal.SizeOf<ResourceHandle>();
    private static readonly int SoundDataOffset = Marshal.SizeOf<nint>();

    private readonly Hook<LoadSoundFileDelegate>? loadSoundFileHook;
    private readonly Hook<PlaySpecificSoundDelegate>? playSpecificSoundHook;
    private readonly HashSet<nint> knownVoiceLinePtrs = [];
    private readonly IPluginLog? log;

    public VoiceLineDetector(ISigScanner sigScanner, IGameInteropProvider gameInterop, IPluginLog? log = null)
    {
        this.log = log;

        if (sigScanner.TryScanText(LoadSoundFileSignature, out var loadSoundFilePtr))
        {
            this.loadSoundFileHook =
                gameInterop.HookFromAddress<LoadSoundFileDelegate>(loadSoundFilePtr, this.LoadSoundFileDetour);
            this.loadSoundFileHook.Enable();
            this.log?.Debug("Hooked into LoadSoundFile");
        }
        else
        {
            this.log?.Error("Failed to hook into LoadSoundFile");
        }

        if (sigScanner.TryScanText(PlaySpecificSoundSignature, out var playSpecificSoundPtr))
        {
            this.playSpecificSoundHook =
                gameInterop.HookFromAddress<PlaySpecificSoundDelegate>(
                    playSpecificSoundPtr, this.PlaySpecificSoundDetour);
            this.playSpecificSoundHook.Enable();
            this.log?.Debug("Hooked into PlaySpecificSound");
        }
        else
        {
            this.log?.Error("Failed to hook into PlaySpecificSound");
        }
    }

    public event Action? VoiceLinePlayback;

    /// <summary>No-op: the hooks are armed in the constructor (they must exist before the
    /// plugin finishes initializing to catch early voice lines).</summary>
    public void Start()
    {
    }

    public void Dispose()
    {
        this.loadSoundFileHook?.Dispose();
        this.playSpecificSoundHook?.Dispose();
    }

    private nint LoadSoundFileDetour(nint resourceHandlePtr, uint arg2)
    {
        var result = this.loadSoundFileHook!.Original(resourceHandlePtr, arg2);

        try
        {
            var fileName = ((ResourceHandle*)resourceHandlePtr)->FileName.ToString();
            if (VoiceLinePathMatcher.IsSoundContainer(fileName))
            {
                var resourceDataPtr = Marshal.ReadIntPtr(resourceHandlePtr + ResourceDataOffset);
                if (resourceDataPtr != nint.Zero)
                {
                    if (!VoiceLinePathMatcher.IsIgnoredSound(fileName))
                    {
                        this.log?.Debug($"Loaded sound: {fileName}");

                        if (VoiceLinePathMatcher.IsVoiceLine(fileName))
                        {
                            this.log?.Debug($"Discovered voice line at address {resourceDataPtr:x}");
                            this.knownVoiceLinePtrs.Add(resourceDataPtr);
                        }
                        else
                        {
                            // Addresses can be reused, so a non-voice-line sound may load to
                            // an address previously occupied by a voice line.
                            if (this.knownVoiceLinePtrs.Remove(resourceDataPtr))
                            {
                                this.log?.Debug(
                                    $"Cleared voice line from address {resourceDataPtr:x} " +
                                    $"(address reused by: {fileName})");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception exc)
        {
            this.log?.Error(exc, "Error in LoadSoundFile detour");
        }

        return result;
    }

    private nint PlaySpecificSoundDetour(nint soundPtr, int arg2)
    {
        var result = this.playSpecificSoundHook!.Original(soundPtr, arg2);

        try
        {
            var soundDataPtr = Marshal.ReadIntPtr(soundPtr + SoundDataOffset);
            // Assume a voice line plays only once after it is loaded; prune as they play.
            if (this.knownVoiceLinePtrs.Remove(soundDataPtr))
            {
                this.log?.Debug($"Caught playback of known voice line at address {soundDataPtr:x}");
                this.VoiceLinePlayback?.Invoke();
            }
        }
        catch (Exception exc)
        {
            this.log?.Error(exc, "Error in PlaySpecificSound detour");
        }

        return result;
    }
}
