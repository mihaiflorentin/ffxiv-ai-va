# GPU engines, accent voices, config v2 (plan)

Date: 2026-10-17. Owner: engine/runtime/config schema. Companion: `docs/plans/2026-10-ui-redesign.md` (UI consumes section 10 of this file).
Scope: no code edits here. Every phase below is written so a subagent can pick it up cold.

## 0. Decisions in one screen

| Topic | Decision |
|---|---|
| GPU backend (primary, all vendors, Windows and Wine) | ONNX Runtime **WebGPU plugin EP** (`Microsoft.ML.OnnxRuntime.EP.WebGpu` 0.4.0, Microsoft, win-x64 + linux-x64). Dawn picks **Vulkan under Wine** (winevulkan -> RADV/NVIDIA/ANV) and **D3D12 on native Windows**. Backend is forced with the provider option `dawnBackendType` = `Vulkan` or `D3D12`. |
| Fallback chain | WebGPU (chosen API) -> DirectML (native Windows only, until WebGPU is proven) -> CPU. Last-resort escape hatch for Linux power users: a **Remote engine** (localhost HTTP) talking to any CUDA/ROCm server. |
| Rejected as primary | DirectML via vkd3d-proton, ORT CUDA/ROCm EPs, llama.cpp Vulkan as the base (kept as a later engine runtime), a shipped native helper process. Reasons in section 2. |
| Engine lineup | CPU tier: Kokoro-82M + accent voice packs (default). GPU tier: Chatterbox Turbo (fp16 / q4f16 on WebGPU) as the expressive and cloning engine. Labs: F5-TTS (non-commercial weights). Retire: legacy Chatterbox. Later: Pocket TTS, Chatterbox Multilingual, Orpheus. |
| Voice identity | Engine-agnostic `VoiceInfo` (id, accent, sex, age, license) with per-engine realizations (`StyleTensor` for Kokoro, `ReferenceClip` for cloning engines). A `voicepack.json` manifest replaces id-prefix parsing. Existing voice ids never change, so `voice-assignments.json`, casting presets and share codes need no migration. |
| Config | `Configuration.Version` 1 -> 2. Typed in-memory migration (`ConfigMigrator`), legacy properties kept nullable for one generation, backup file, change report persisted until the UI acknowledges it. |
| Spike first | S0: WebGPU EP on this Linux box (RX 9070 XT, native Vulkan) with Kokoro and Turbo graphs. S1: the same binary under the XIVLauncher Wine. Both are one-day, no plugin changes. |

## 1. Verified findings (what the code and the machine say)

| # | Finding | Evidence |
|---|---|---|
| F1 | Kokoro, the default engine, never consults `EpSelector`; it builds bare CPU `SessionOptions`. Only Chatterbox, Turbo and F5 try DirectML. | `Infrastructure/Kokoro/KokoroSynthesizer.cs:371-383`; `Onnx/ChatterboxSynthesizer.cs:643-670`; `F5/F5Synthesizer.cs:330-347` |
| F2 | The expressive engine is hard-wired to CPU: `executionProvider: "cpu"`. | `Container/ServiceContainer.cs:404` |
| F3 | Chatterbox's LM decode loop round-trips the whole KV cache through managed CPU tensors every token (clone per step). On a GPU that is a PCIe copy per token, so a naive `EpSelector` swap will be slow. | `ChatterboxSynthesizer.cs:431-485` |
| F4 | The owner's Wine prefix has 116 KB `d3d12.dll` / 37 KB `d3d12core.dll`: Wine's built-in vkd3d stubs, not vkd3d-proton. XIVLauncher.Core ships DXVK only (FFXIV is D3D11). DirectML needs a full D3D12 device, so it cannot work there without the user hand-installing vkd3d-proton. | `~/.xlcore/wineprefix/drive_c/windows/system32/d3d12*.dll`; `~/.xlcore/compatibilitytool/dxvk/` only |
| F5 | The prefix has `vulkan-1.dll` + `winevulkan.dll`; the host has libvulkan and RADV. A Windows process under this Wine can already reach Vulkan (DXVK does it). | `ls system32`, `/usr/lib/libvulkan_radeon.so` |
| F6 | Hardware here: Radeon RX 9070 XT (Navi 48, RDNA4) plus the Ryzen iGPU (Radeon 610M). No NVIDIA, no ROCm install (`/opt/rocm` absent). | `lspci` |
| F7 | WebGPU EP 0.4.0 package: win-x64 `onnxruntime_providers_webgpu.dll` 11 MB, `dxcompiler.dll` 18 MB, `dxil.dll` 1.5 MB (about 13.5 MB zipped). Needs core ORT >= 1.24.4 (the plugin pins exactly 1.24.4). Linux needs `libvulkan.so.1`. Registered at runtime with `OrtEnv.RegisterExecutionProviderLibrary(name, path)` then `SessionOptions.AppendExecutionProvider(env, devices, options)`. | nupkg listing and README (fetched 2026-10-17) |
| F8 | The WebGPU EP option header defines `ep.webgpuexecutionprovider.dawnBackendType` with values `D3D12` / `Vulkan`, plus `powerPreference`, `deviceId`, `adapterIndex`, `preferredLayout`, `maxNumPendingDispatches`, `enableGraphCapture`, buffer-cache modes. The docs page does not mention the backend option; the header does. Whether the plugin form wants the `ep.webgpuexecutionprovider.` prefix is unverified (S0 checks). | `onnxruntime/core/providers/webgpu/webgpu_provider_options.h` (main) |
| F9 | `Microsoft.ML.OnnxRuntime.DirectML` stops at 1.24.4; core ORT is at 1.31.0. DirectML is a dead end for new ORT versions. | NuGet index |
| F10 | Plugin zip is already 156 MB (MisakiSharp 69 MB, KokoroSharp voices 79 MB, DirectML 18.5 MB, onnxruntime 17 MB). No hard limit is known for a custom repo (not verified for the official repo). WebGPU adds about 13.5 MB zipped. | `ls -la` of build output |
| F11 | Dalamud serializes the config with Newtonsoft and `TypeNameHandling` (the real file starts `"$type": "AIVoiceActing.PluginConfiguration, AIVoiceActing"`, nested presets carry `$type` too). Type names are therefore a persisted contract. | `~/.xlcore/pluginConfigs/AIVoiceActing.json` |
| F12 | The owner's live config has `SelectedEp: "coreml"`, `UseFp32LanguageModel: true`, `SelectedEngine: "kokoro"`, `ExpressiveEngine: false`. Migration must cope with `coreml` on a Windows/Wine host. | same file |
| F13 | Official Turbo ONNX ships fp32, fp16, q4, q4f16 and int8 variants. Sizes: fp32 about 3.3 GB, **fp16 about 1.66 GB**, **q4f16 about 0.56 GB**. | HF API tree of `ResembleAI/chatterbox-turbo-ONNX` |
| F14 | Pocket TTS (Kyutai) weights are **CC-BY-4.0** on the model card (code is MIT). Official release is safetensors; ONNX exports are community uploads of unknown quality. | HF API `kyutai/pocket-tts` |
| F15 | There is no official ONNX of Orpheus; community exports exist but are unvetted. | HF search |
| F16 | `voices.json` already contains a parked `disabledSets` block (Icelandic, Scottish, Scouse, Nordic, Japanese flavours, many beast sets) waiting for voices. Packs unpark it as a data change. | `Domain/voices.json`, `RaceVoiceMap.cs` comment |
| F17 | `VoiceCatalog.Describe` derives language and sex from the Kokoro two-letter id prefix; no slot exists for Irish, Welsh, Australian, Indian. | `Domain/VoiceCatalog.cs:38-72` |
| F18 | Cloning engines already borrow Kokoro voices by rendering a 10 s Kokoro reference clip per voice and pitch (`voices-kokoro-ref`) and cloning it. | `ServiceContainer.cs:342, 443-480` |

## 2. GPU backend options (evaluated)

| Option | Works under Wine? | Vendors | Ships in plugin? | Effort | Verdict |
|---|---|---|---|---|---|
| **A. ORT WebGPU plugin EP, Dawn Vulkan backend** | Expected yes: Dawn loads `vulkan-1.dll` (Wine's, F5), no D3D12 involved. Unproven until S1. | AMD, NVIDIA, Intel (any Vulkan 1.1+ driver) | Yes, +13.5 MB zipped, same ORT core | Low-medium; same `InferenceSession` code path | **Primary** |
| A'. Same EP, Dawn D3D12 backend | Native Windows only | all | same package | free | Used on native Windows (needs `dxcompiler.dll`/`dxil.dll`) |
| B. DirectML (current) | No: needs real D3D12; Wine's vkd3d stub is not enough (F4). With hand-installed vkd3d-proton it might work, but that is a per-user prefix hack, unverifiable by us, and DML is frozen at ORT 1.24.4 (F9) | all on native Windows | Yes (18.5 MB) | none | Keep on native Windows as fallback for one release; plan removal after A' proves out |
| C. vkd3d-proton instructions for users | Fragile, wrong place to put reliability | | No | | Reject (mention in docs only as "DirectML on Wine is unsupported") |
| D. Out-of-process helper per OS (CUDA/ROCm/Vulkan, localhost) | Yes if the user can launch a Linux process; from inside Wine one can spawn an ELF, but Flatpak XIVLauncher and Steam Deck sandboxes break this | best raw speed (ROCm/CUDA) | Separate downloads per OS, support burden | High | **Not shipped.** Provide the client side only (Remote engine, section 6 phase 6) so power users can run any server. |
| E. llama.cpp / ggml Vulkan DLL in-process | Probably (same winevulkan path as A) | all | +tens of MB | High: new runtime, C ABI binding, SNAC decoder as a second ONNX model | Later, only if an LLM-style TTS (Orpheus/NeuTTS) earns a slot. Reuses A's Vulkan proof. |
| F. ORT CUDA EP | Needs NVIDIA CUDA/cuDNN toolkit files (GBs) and nvcuda inside Wine | NVIDIA only | No | | Reject |
| G. ORT ROCm/MIGraphX | No Windows build usable in Wine | AMD only | | | Reject |
| H. OpenVINO EP | Intel only, not Wine-friendly | | | | Reject |

Owner-specific note: the box has an iGPU next to the dGPU. TTS on the iGPU can avoid contending with the game renderer. The adapter picker (config `Device.AdapterId`) must therefore list every adapter, not only the "best" one, and the benchmark (phase 3) should time both.

Packaging: bundle `onnxruntime_providers_webgpu.dll` (+ `dxcompiler.dll`, `dxil.dll`) in the zip through the existing `PreparePackOutput` target in `AIVoiceActing.csproj`; load by absolute path (Dalamud gives an empty `Assembly.Location`, so `WebGpuEp.GetLibraryPath()` is unusable; extend `NativeLibraryPreloader.EngineNatives` and pass `PluginInterface.AssemblyLocation`). Models stay out of the zip and download through `HuggingFaceProvisioner`. If the zip size becomes a problem, trim non-English Kokoro voices that no casting set references into an optional pack (phase 2, step 8).

Keep ORT at 1.24.4 for now (DirectML and WebGPU plugin both satisfied). Revisit "ORT 1.31 + WebGPU only, drop DirectML" as phase 7 once A' is proven; newer ORT likely has better WebGPU operator coverage.

## 3. Engine lineup

| Engine id | Tier | Role | Footprint | License | Notes |
|---|---|---|---|---|---|
| `kokoro` | CPU (GPU optional after S0) | Default for all lines; accent packs add voices | 325 MB fp32 + packs | Apache-2.0 | Style tensors are the voice. Stock 28 English voices plus pack voices. |
| `chatterbox-turbo` | GPU recommended (CPU works, slow) | Expressive lines, tags (`[laugh]`), cloning of reference clips (real accents) | fp16 1.66 GB, q4f16 0.56 GB, fp32 3.3 GB | MIT | Replaces both the legacy engine and the CPU-only expressive path. Optional "primary for all lines" for GPU owners. |
| `f5` | Labs | Existing opt-in | 1.45 GB | **CC-BY-NC weights**: UI must show "personal use only" | Keep, demote to Advanced, no new work. Candidate for removal (drops `Horus.F5Tts.Onnx`). |
| `chatterbox` (legacy) | Retired | | | | Migrated to `kokoro`; code deleted in phase 5 (the Turbo path of `ChatterboxSynthesizer` stays). |
| `remote` | Escape hatch | OpenAI-style HTTP to a user-run server | none | n/a | Phase 6. |
| `pocket-tts` | Later (CPU) | 100M cloning on CPU, fast | small | **CC-BY-4.0** (attribution UI) | Only after a spike validates a community ONNX (F14). |
| `chatterbox-multilingual` | Later (GPU) | Adds the exaggeration knob Turbo lacks | about 0.5B | MIT | After Turbo-on-GPU works. |
| Orpheus 3B, IndexTTS-2, Zonos, CSM-1B, Spark-TTS, NeuTTS | Watch list | | | Orpheus Apache-2.0; IndexTTS-2 needs contact for commercial use; Zonos has no official ONNX | Revisit when an ONNX or GGUF path plus a Vulkan runtime (option E) exists. |

Recommended defaults by hardware (encoded in `SetupAdvisor`, phase 5): no usable GPU -> Kokoro CPU. GPU present -> Kokoro on its recommended device plus Expressive routing to Turbo (fp16; q4f16 when the adapter is an iGPU or reports small limits). The wizard offers "Cinematic" (Turbo for all lines) only if the phase-5 benchmark shows RTF below 0.8 on the chosen adapter.

## 4. Architecture changes

### 4.1 Ports and domain (new or changed)

| Item | Layer | Shape |
|---|---|---|
| `IEngineCatalog` + `EngineInfo` | Ports/EngineCatalog.cs | `Key, DisplayName, Blurb, Speed (Fast/Slower/Slow), Tier (Cpu/Gpu/Labs/Remote), SupportsGpu, DefaultDevice (Cpu/Gpu), Consumes (RealizationKind[]), License, NonCommercial, Experimental, AssetGroups[], ApproxBytesByPrecision`. Static data in Infrastructure (`EngineCatalog`), so group keys stop being string literals in the UI. Legacy aliases: `turbo` -> `chatterbox-turbo`. |
| `IDeviceProbe` + `DeviceReport` | Ports/DeviceProbe.cs | `Host (WindowsNative / WindowsOnWine / Other), Adapters[{AdapterId, Name, Vendor, Kind (Discrete/Integrated/Unknown), Backends[]}], Backends[{Id, Available, Reason}], SelfTest?` . Adapter ids are stable hashes of vendor id + device id + name (Dawn adapter indexes are not stable). |
| `EngineStatus` | Ports/SpeechSynthesizer.cs | `State (NotInstalled/Loading/Ready/FellBack/Failed), RequestedDevice, ActiveDevice (Cpu/Gpu + adapter name + api), Reason, CanRetryGpu, LastRtf, LinesRendered`. Exposed as `ISpeechSynthesizer.Status` (snapshot, polled per frame like `IsReady` today) plus `Task RetryAsync(DeviceMode, ct)`. `IsReady`/`NotReadyReason` stay as thin views. |
| `SynthesisRequest.Voice` | Ports | Replaces the free-form `ReferenceVoiceId` string with `VoiceId` (stable id) + `ResolvedVoice? Voice` (realization: kind, absolute path, transcript, phonemizer lang). A `RealizingSynthesizer` decorator (Domain) resolves it before the engine sees it. This deletes `ClippingResolver` and `PrepareExpressiveVoiceAsync` from `ServiceContainer` (the Kokoro->clip bridge moves into `ClipBridge`, an Infrastructure service). |
| `IVoiceLibrary` + `VoiceInfo`, `VoicePackInfo`, `PackProblem` | Ports/VoiceLibrary.cs | See 4.3. |
| `IConfigMigrationReport` | Ports | Read-only view over `Configuration.Migration` (see 5.4). |
| `IModelProvisioner` additions | Ports | `long? FreeBytes(string dir)`; `DownloadAsync` resumes from `.part` via HTTP Range; asset gets `Precision` and `LocalPath`. |

Domain stays pure (no ORT/Dalamud): `ConfigMigrator` rules, `SetupAdvisor`, `RealizingSynthesizer`, `VoicePackManifest` (parse + validate), `Accents` table, `DeviceModePolicy` (maps `Device.Mode`/engine default/probe to a `ComputePlan`). `ArchitectureScanTests` keeps passing because ORT types stay in `Infrastructure/Onnx`.

### 4.2 Compute plumbing (Infrastructure/Onnx)

| Component | Responsibility |
|---|---|
| `RuntimeHost` | Detects Wine by `GetProcAddress(ntdll, "wine_get_version")` (and `wine_get_host_version` for the host OS). Injectable in tests. |
| `WebGpuRuntime` | One-time `RegisterExecutionProviderLibrary` by absolute path; `EnumerateAdapters()` from `GetEpDevices()`; caches result; survives plugin reload (library stays registered for the process; guard against double registration). |
| `OnnxSessionFactory` (replaces `EpSelector`, which stays as a thin shim until tests are ported) | `Create(ComputePlan, role)` returns `SessionOptions` + effective backend. `WebGpuOptions.Build(plan)` is a pure function returning the option dictionary (testable without a GPU): `dawnBackendType`, `powerPreference`, `adapterIndex`, `preferredLayout`, `maxNumPendingDispatches` from `GpuImpact`, buffer cache `bucket`. Roles let one engine split sessions across devices (e.g. Turbo LM on CPU, encoder and decoder on GPU). |
| `GpuCrashGuard` | Writes `gpu-session.pending` before creating the first GPU session and deletes it after the first successful inference. If the marker exists at startup, the previous run died in native GPU code: force CPU, set `Migration`-style notice "GPU disabled after a crash; [Retry]". Dawn or driver faults kill the whole game process, so this loop breaker is mandatory, not optional. |
| `DeviceProbe` | Implements `IDeviceProbe` over `RuntimeHost` + `WebGpuRuntime` + DirectML presence check. `SelfTest` runs a small ONNX op graph (embedded 2 KB model) on the chosen adapter and reports ms; failure becomes the fallback reason string. |

### 4.3 Voice model

**Identity** (`VoiceInfo`): `Id, DisplayName, Accent (code + label), Sex, AgeBand, Description, Tags[], Quality (stock/verified/experimental), PackId, License (spdx, attribution, url), PreviewFile?, Realizations[]`.
**Realization kinds**: `StyleTensor` (Kokoro `.npy`, 510x1x256 float32, with `phonemizer` = `a` or `b`), `ReferenceClip` (wav 24 kHz mono, 5-15 s, optional transcript for F5), `EnginePreset` (engine-native named voice, for Pocket TTS later).
**Resolution rule** (`RealizingSynthesizer`): voice id -> realization of a kind the active engine consumes -> else bridge (Kokoro-rendered clip via `ClipBridge` for cloning engines, as today) -> else the engine default voice, with a one-line log. A voice missing from the library (pack removed) keeps its id in profiles; `RaceVoiceMap`'s existing "retired voice" reassignment (`ServiceContainer.cs:~916`) now asks `IVoiceLibrary.Has(id)`, and `RaceVoiceMap.SlotsFor` drops slots whose voice is unavailable before falling back to the group base set. No stock id is renamed, so stored ids stay valid.
**Legacy fallback**: `VoiceCatalog.Describe` becomes the internal parser for bare ids that no manifest claims (user-dropped `.npy` files). The core manifest (`Domain/voices-core.json`, embedded, generated by the tool) lists all 61 KokoroSharp voices with accent, sex and Kokoro quality grade, so UI filters stop depending on prefixes.

**Pack layout** (`voicepacks/<packId>/`, in the plugin dir for bundled packs, `<ConfigDir>/voicepacks/<packId>/` for user packs, additional roots from `Voices.ExtraPackDirs`):

```
voicepack.json   LICENSE.txt   NOTICE.txt   build.lock.json
style/<slug>.npy        clips/<slug>.wav   clips/<slug>.txt   preview/<slug>.wav
```

```json
{
  "schema": 1, "id": "aiva-isles", "version": "1.0.0", "name": "British Isles accents",
  "license": { "spdx": "CC-BY-SA-4.0", "attribution": "Voices derived from ylacombe/english_dialects ...", "url": "..." },
  "voices": [{
    "id": "isles.ie_m_01", "name": "Irish man, warm", "accent": "en-IE", "sex": "male", "age": "adult",
    "tags": ["warm", "soft"], "description": "...", "quality": "experimental",
    "previewFile": "preview/ie_m_01.wav",
    "realizations": [
      { "kind": "StyleTensor", "file": "style/ie_m_01.npy", "phonemizer": "b" },
      { "kind": "ReferenceClip", "file": "clips/ie_m_01.wav", "transcript": "clips/ie_m_01.txt" }
    ],
    "source": { "dataset": "ylacombe/english_dialects", "revision": "<sha>", "speaker": "...", "license": "CC-BY-SA-4.0" }
  }]
}
```

Rules enforced by the loader (each one is a test): schema version known; ids unique across all packs (first pack wins, the loser becomes a `PackProblem`); id charset `[a-z0-9._-]+` and not shadowing a stock id; every `file` stays inside the pack directory (reject `..`, rooted paths, symlink escapes); `.npy` shape and dtype checked on load; missing files demote the voice to "unavailable" with a reason instead of failing the pack; a pack with no `license` is rejected.

**Accent picks the G2P, not the id.** KVoiceWalk optimises against the American phonemizer, but Isles accents may sound better phonemized as British. The manifest's `phonemizer` field chooses it. How KokoroSharp derives language from a voice (file/name prefix) decides the mechanism; spike S3 answers it. If it keys off the name, the loader registers the tensor under an internal name such as `bm__isles_ie_m_01` while the public id stays `isles.ie_m_01`.

**Sources for accents** (verify each license at build time; none of this is legal advice):

| Accent set | Source | License | Caveat |
|---|---|---|---|
| Irish, Scottish, Welsh, Northern, Midlands, Southern English, M/F | `ylacombe/english_dialects` (owner verified) | CC-BY-SA-4.0 | ShareAlike: ship the pack as a separate data collection with its own LICENSE and NOTICE; show attribution in the UI; keep it out of the code license. Owner should confirm the SA reading for derived style tensors. |
| Australian, Indian, South African, NZ, Canadian, Northern Irish | VCTK corpus (Edinburgh DataShare) is the best candidate; Mozilla Common Voice (CC0, accent tags, noisy) as backup | VCTK v0.92 is CC-BY-4.0 per my recollection, **unverified**; check the DataShare record | Studio quality for VCTK; Common Voice needs clip curation. |
| Extra variety | Kokoro voice blending (KokoroSharp mixes style tensors) of stock + pack voices, with pitch/speed variants | inherits | Cheap way to reach 60+ distinct voices; mark as `derived`. |

**Quality gate in the build tool**: KVoiceWalk Target Sim >= 0.55 and Self Sim >= 0.95 (the owner's run: 0.53-0.62 / 0.97-0.99), a rendered audition sheet (3 fixed lines per voice), and a human "keep/drop" checklist stored in `build.lock.json`. Quality field stays `experimental` until a listening pass promotes it.

**Dev tool** `tools/voicepack/` (Python via `uv`, mirrors the existing `~/.xlcore/kvoicewalk` setup, not shipped): `fetch_clips.py` (pinned dataset revision, picks clean 20-30 s and 8-12 s segments), `walk.sh` (pinned KVoiceWalk commit + seed), `pt_to_npy.py` (510x1x256 float32 check), `build_pack.py` (assemble dir, manifest, preview wavs through SmokeSynth, license files), `README.md`. `SmokeSynth` gains `--validate-pack <dir>` and `--audition <dir>` that reuse `FileVoiceLibrary` so the CLI and the plugin share one validator.

### 4.4 Provisioning

| Change | Detail |
|---|---|
| Data-driven catalog | `ModelCatalog` constants become an embedded `models-catalog.json` (asset: engine, group, precision, url, size, sha256, `localPath`, optional). Adding an engine or precision becomes data. Existing downloaded files keep their exact local paths (flat `kokoro-v1.0.onnx`, `turbo-*` fp32) so nobody re-downloads 3 GB; new assets use `models/<engine>/<precision>/...`. |
| Precision groups | Turbo: `fp16` (1.66 GB), `q4f16` (0.56 GB), `fp32` (existing). The advisor picks; the UI offers all. |
| Resume + disk | HTTP Range resume for `.part`; `FreeBytes` before queueing; sha256 stays mandatory for LFS assets. |
| Packs | Bundled packs need no download. Later (phase 7) `packs-index.json` (pinned URL, sha256, size, license) + zip install with zip-slip protection and manifest validation before activation. |
| Runtime natives | Shipped in the plugin zip (no runtime download of unsigned DLLs). |

## 5. Configuration v2

### 5.1 Schema

Flat properties that stay as they are (names unchanged, same defaults): everything not listed below, including `Enabled`, `GlobalVolume`, keybinds, quest/battle/cutscene toggles, chat filters, name-with-say, rate limiter, `RemoveStutter`, `UseRaceVoicePresets`, `Lexicons`, style tags, `EnabledChatTypesPresets`, `CurrentPresetId`, `DefaultExaggeration`, `DirectorEnabled`, `KeepTempoWhenPitched`, `LevelVoiceLoudness`, `AgeAwareCasting`, `StreamSentences`, `ShowNowSpeaking`, `StaleLineSeconds`, `Triggers`, `Exclusions`.

```csharp
public const int CurrentVersion = 2;
public int Version { get; set; } = CurrentVersion;      // fresh installs start current; files carry their own

public EngineSettings Engine { get; set; } = new();
public DeviceSettings Device { get; set; } = new();
public VoiceSettings Voices { get; set; } = new();
public MigrationState Migration { get; set; } = new();

// UI-owned additive fields (declared by the UI plan, listed here so the schema has one owner)
public bool ShowAdvanced { get; set; }
public bool ReduceMotion { get; set; }
public string LastPage { get; set; } = "";
public int OnboardingCompletedVersion { get; set; }
public int OnboardingStep { get; set; }
public int MigrationNoticeAckedVersion { get; set; }

public sealed class EngineSettings
{
    public string Primary { get; set; } = "kokoro";                 // engine id; aliases resolved by the catalog
    public ExpressiveSettings Expressive { get; set; } = new();
    public Dictionary<string, EngineOptions> PerEngine { get; set; } = new();   // advanced overrides only
}
public sealed class ExpressiveSettings { public bool Enabled { get; set; } public string EngineId { get; set; } = "chatterbox-turbo"; }
public sealed class EngineOptions
{
    public string Device { get; set; } = "inherit";     // inherit | cpu | gpu
    public string Precision { get; set; } = "auto";     // auto | fp32 | fp16 | q4f16
    public Dictionary<string, string> Extra { get; set; } = new();   // e.g. remote: url, model, voiceMap
}
public sealed class DeviceSettings
{
    public string Mode { get; set; } = "auto";          // auto | gpu | cpu   (auto = each engine's recommended device)
    public string AdapterId { get; set; } = "";         // "" = probe's default (high-performance); else DeviceReport adapter id
    public string Backend { get; set; } = "auto";       // advanced: auto | webgpu | directml
    public string Api { get; set; } = "auto";           // advanced, webgpu only: auto | vulkan | d3d12  (auto = vulkan on Wine, d3d12 on native Windows)
    public string GpuImpact { get; set; } = "balanced"; // gentle | balanced | fast  (pending-dispatch cap + chunk pacing)
    public string CpuImpact { get; set; } = "medium";   // low | medium | high  (moved from the top level, same meaning)
    public bool FallbackToCpu { get; set; } = true;
    public bool GpuDisabledByCrashGuard { get; set; }   // set by GpuCrashGuard; cleared by "Retry on GPU"
}
public sealed class VoiceSettings { public List<string> DisabledPacks { get; set; } = []; public List<string> ExtraPackDirs { get; set; } = []; }
public sealed class MigrationState
{
    public int FromVersion { get; set; }
    public List<MigrationChange> Changes { get; set; } = [];   // pending until the UI acknowledges; cleared with MigrationNoticeAckedVersion
}
public sealed record MigrationChange(string Area, string Before, string After, string Message);
```

Legacy v1 properties stay on the class as **nullable, `[Obsolete]`, written as null**: `string? SelectedEngine`, `string? SelectedEp`, `string? CpuImpact` (top level), `bool? ExpressiveEngine`, `bool? UseFp32LanguageModel`. They exist only so both serializers (System.Text.Json in tests, Newtonsoft in Dalamud) still read old files into them. Remove in schema v3. Nested type names (`AIVoiceActing.EngineSettings, AIVoiceActing`, etc.) are persisted by Newtonsoft (F11): never rename them without a migration.

### 5.2 Migration rules (v1 -> v2)

`ConfigMigrator.Migrate(Configuration c, MigrationContext ctx)` where `MigrationContext(bool IsWine)`; returns `ConfigMigrationResult(bool Changed, IReadOnlyList<MigrationChange>)`. Pure, idempotent, runs only when `c.Version < CurrentVersion`.

| v1 field and value | v2 result | Report line |
|---|---|---|
| `SelectedEngine` `kokoro`, null, empty | `Engine.Primary = "kokoro"` | none |
| `turbo` | `Engine.Primary = "chatterbox-turbo"` | "Engine id renamed" |
| `f5` | `Engine.Primary = "f5"` | "F5 weights are non-commercial; moved to Labs" |
| `chatterbox` (legacy) | `Engine.Primary = "kokoro"` | "Legacy Chatterbox was retired; switched to Kokoro. Old files can be deleted on the Models page" |
| any other string | `kokoro` | "Unknown engine X replaced by Kokoro" |
| `SelectedEp` `auto` or null | `Device.Mode = "auto"` | none |
| `cpu` | `Device.Mode = "cpu"` | none |
| `directml`, native Windows | `Device.Mode = "gpu"`, `Device.Backend = "directml"` | "GPU choice kept (DirectML)" |
| `directml`, Wine | `Device.Mode = "auto"` | "DirectML cannot run under Wine; now using automatic GPU selection" |
| `coreml` or anything else | `Device.Mode = "auto"` | "CoreML was never available in game; now automatic" (owner's file hits this, F12) |
| `CpuImpact` low/medium/high | `Device.CpuImpact` same value; other -> `medium` | none |
| `ExpressiveEngine` true/false/null | `Engine.Expressive.Enabled`; `EngineId = "chatterbox-turbo"` | none |
| `UseFp32LanguageModel` | dropped | only when the old engine was `chatterbox` |
| `Version` | `2`; `Migration.FromVersion = 1` | none |

After mapping: legacy fields set to null; `Migration.Changes` filled; `MigrationNoticeAckedVersion` untouched (UI shows the banner while `Changes.Count > 0`). Versions above `CurrentVersion` are left untouched with a log line (downgrade-safe, no destructive writes). Defaults decision: **Kokoro runs on CPU by default** (descriptor `DefaultDevice = Cpu`) until S0 shows a clear GPU win; Turbo defaults to GPU, so `Device.Mode = auto` is safe for upgraders.

### 5.3 Load flow in the plugin entry

1. `PluginInterface.GetPluginConfig() as PluginConfiguration ?? new()`.
2. `ConfigMigrator.Migrate(config, new(IsWine: RuntimeHost.IsWine))`.
3. If changed: copy `PluginInterface.ConfigFile` to `AIVoiceActing.json.v1.bak` (only if the backup does not exist), then `SavePluginConfig`. Downgrade = restore the backup.
4. Evaluate `GpuCrashGuard`; if tripped, `Device.GpuDisabledByCrashGuard = true` and add a pending change.
5. Build the container from `config.Engine` / `config.Device` factories.

Other persisted files need no migration: `voice-assignments.json`, `casting-presets.json`, share codes keep stock ids; pack voice ids are new. The extracted `voices.json` is overwritten on content drift already (`ExtractVoicesManifest`).

### 5.4 Migration tests (`tests/AIVoiceActing.Tests/ConfigMigrationTests.cs`)

| Test | Asserts |
|---|---|
| `Owner_file_fixture` | Sanitized copy of the real v1 file (`Fixtures/config-v1-owner.json`, `coreml`, fp32 flag) migrates to `Mode=auto`, `Primary=kokoro`, one change line, flat props unchanged. |
| `Engine_mapping` (Theory, 7 rows above) | primary id + change line presence. |
| `Ep_mapping` (Theory, rows x {Wine, native}) | Mode, Backend, line text key. |
| `CpuImpact_and_Expressive` | moved values; unknown CpuImpact -> medium. |
| `Flat_properties_survive` | Reuse `ConfigurationTests.Mutated()`; every untouched property equal after migrate and re-serialize. |
| `Idempotent` | second migrate returns `Changed=false`, same JSON. |
| `Future_version_untouched` | Version 3 file stays byte-equal, no changes. |
| `Missing_sections` | v1 JSON without new sections deserializes to defaults. |
| `Newtonsoft_typed_roundtrip` | A `$type`-carrying v1 fixture deserialized with Newtonsoft (`TypeNameHandling.Objects`, binder to a test subclass) migrates identically to the System.Text.Json path. Adds `Newtonsoft.Json` as a **test-only** package reference (Dalamud supplies it at runtime). |
| `Fresh_install_is_current` | `new Configuration().Version == CurrentVersion`, no changes recorded. |
| `Ack_clears_pending` | Clearing `Migration.Changes` persists through round trip. |

## 6. Phases (each is a separate PR)

Order: phase 0 gates 3, 5, 6. Phases 1, 2 and 4 do not depend on the GPU result and can start immediately and in parallel (separate files). Agent column follows the repo routing (implementer = contained change with a full spec, engineer = subtle/multi-module).

| # | Phase | Agent | Depends on |
|---|---|---|---|
| 0 | Spikes S0-S5 (section 7) | engineer | none |
| 1 | Config v2 + migrator + engine catalog skeleton | implementer | none |
| 2 | Voice library, packs, accent tool | engineer (library + Kokoro) then implementer (pack content) | 1 for ids; S3 |
| 3 | Device probe, WebGPU runtime, crash guard, Kokoro device wiring | engineer | S0, S1 |
| 4 | Provisioning refactor (data catalog, precision, resume, disk) | implementer | 1 |
| 5 | Turbo on GPU, expressive routing rework, SetupAdvisor, legacy Chatterbox removal | engineer | 3, 4, S2 |
| 6 | Remote engine + reference server | implementer | 1 (can ship earlier if S1 fails) |
| 7 | Extra engines, DirectML/ORT upgrade decision, downloadable pack index | separate plans | 5 |

### Phase 1 - Config v2, migrator, engine catalog skeleton

Files: new `src/AIVoiceActing/ConfigurationSections.cs`, `ConfigMigrator.cs`; edit `Configuration.cs`, `AIVoiceActingPlugin.cs` (load flow 5.3, factories at lines ~129-200 now read `config.Engine.Primary`, `config.Device.CpuImpact`, `config.Engine.Expressive.Enabled`), `Container/ServiceContainer.cs` (rename `selectedEngineFactory`/`selectedEpFactory` consumers; add `engineCatalog`), new `Ports/EngineCatalog.cs` + `Infrastructure/EngineCatalog.cs` (static data, aliases), compile-only touches in `UI/ConfigurationWindow.cs` (about 10 sites) and `UI/State/ModelsTabModel.cs`. The UI plan rewrites those files; coordinate by landing this phase first and touching only property accesses.
Tests: section 5.4; `EngineCatalogTests` (aliases, every catalog asset group belongs to exactly one engine); update `ConfigurationTests` (Version default, `Mutated()` additions); `ContainerWiringTests` for the renamed factories.
Acceptance: `dotnet test` green; the owner's real file migrates with one change line and identical audible behaviour (Kokoro CPU); a `.v1.bak` exists after first load; `ArchitectureScanTests` green; no behaviour change otherwise.

### Phase 2 - Voice library and accent packs

1. Ports/VoiceLibrary.cs (exceptions, DTOs, interface); Domain: `VoicePackManifest` (parse/validate), `Accents`, `RealizingSynthesizer`.
2. Infrastructure/Storage/`FileVoiceLibrary` (scan roots, validation rules 4.3, `Reload()`); embedded `Domain/voices-core.json` generated by the tool.
3. Kokoro adapter: load a realization's `.npy` into the voice manager (internal name mapping from S3); `ResolveVoice` takes `ResolvedVoice`; keep the current fallback to `af_heart`.
4. Container: `VoiceLibrary` property, decorator wiring, remove `ClippingResolver`/`PrepareExpressiveVoiceAsync` by moving the bridge into `Infrastructure/Audio/ClipBridge.cs` (behaviour-preserving; same cache folder `voices-kokoro-ref`).
5. `RaceVoiceMap.SlotsFor` availability filter; unpark Scottish/Irish/Welsh sets in `voices.json` as data changes referencing pack ids (keep the `disabledSets` block as the file demands).
6. `VoiceCatalog` demoted to the legacy parser used by the library; UI code keeps compiling until the UI plan P5.
7. Tooling (`tools/voicepack/`, `SmokeSynth --validate-pack/--audition`), first pack `aiva-isles` (Irish, Scottish, Welsh, Northern, Midlands, Southern, M/F, 2-3 voices each, about 15 voices) bundled in the zip through a csproj `Content` item (`voicepacks\**`). Australian/Indian pack follows once the VCTK license is verified.
8. Optional zip trim: move non-English Kokoro voices that no `voices.json` set references into `aiva-kokoro-extra` (downloadable in phase 7); only do this after counting references.
Tests: `VoicePackManifestTests` (valid, bad schema, duplicate ids, path traversal, missing license, bad charset), `FileVoiceLibraryTests` (temp dirs: two packs, collision, disabled pack, missing file demotes voice, reload picks up a dropped pack), `RealizingSynthesizerTests` (kind selection, bridge, default fallback, missing voice), `RaceVoiceMapTests` additions (unavailable slot filtered, empty set falls back), `KokoroSynthesizerTests` addition (custom tensor path, with the existing fake pattern), `ArchitectureScanTests` unchanged.
Acceptance: dropping a valid pack folder into `<ConfigDir>/voicepacks/` makes its voices selectable without a restart (after `Reload`); removing it leaves speakers on a fallback voice with a log line, no exception; `SmokeSynth --audition` renders every voice of the bundled pack; attribution text is reachable through `VoiceInfo.License`.

### Phase 3 - Device probe and WebGPU runtime

Files: `AIVoiceActing.csproj` (add `Microsoft.ML.OnnxRuntime.EP.WebGpu` 0.4.0 with `ExcludeAssets` on its managed helper if it tries to resolve the library path itself; extend `PreparePackOutput` to flatten `onnxruntime_providers_webgpu.dll`, `dxcompiler.dll`, `dxil.dll`), `packages.lock.json` (both projects), `Infrastructure/Onnx/{RuntimeHost,WebGpuRuntime,WebGpuOptions,OnnxSessionFactory,GpuCrashGuard,DeviceProbe}.cs`, `NativeLibraryPreloader.cs`, `Ports/DeviceProbe.cs`, `Domain/DeviceModePolicy.cs`, `KokoroSynthesizer.cs` (session options from the factory; honour `PerEngine["kokoro"]`), `ServiceContainer.cs` (probe + status), `tests/.../AIVoiceActing.Tests.csproj` (compile the new Onnx sources already via the glob), `tools/SmokeSynth` (flags `--device cpu|gpu --api vulkan|d3d12 --adapter N`, `--bench`).
Tests: `WebGpuOptionsTests` (option dictionary for every mode/api/impact), `DeviceModePolicyTests` (table: mode x engine default x probe result -> plan, including "Wine -> vulkan", "no adapter -> cpu with reason"), `RuntimeHostTests` (injected exports), `GpuCrashGuardTests` (marker lifecycle in a temp dir), `DeviceProbeTests` with a fake runtime; GPU integration tests behind `AIVA_GPU_TESTS=1` (skipped in CI).
Acceptance: on this box, native Linux `SmokeSynth --device gpu` and the in-game build under Wine both report the RX 9070 XT on Vulkan; with the guard marker present the plugin starts on CPU with a pending change; every failure path yields `EngineStatus.State = FellBack` with a human reason; Kokoro lines still render when the GPU session throws.

### Phase 4 - Provisioning refactor

Files: new `Infrastructure/Onnx/models-catalog.json` (embedded) + loader replacing the constants in `ModelCatalog.cs` (keep the class as a facade so `ModelCatalogTests` and the container keep compiling), `Net/HuggingFaceProvisioner.cs` (Range resume, `FreeBytes`), `Ports/ModelStore.cs` (`ModelAsset` gains `Precision`, `LocalPath`), `Storage/FileModelStore.cs`.
Tests: extend `HuggingFaceProvisionerTests` (resume continues from a partial `.part`, server ignoring Range restarts cleanly, hash mismatch deletes `.part`), `ModelCatalogTests` (every asset has size and sha for LFS files, no two assets share a `LocalPath`, existing local paths unchanged vs the old constants), `FileModelStoreTests`.
Acceptance: an existing install with fp32 Turbo and Kokoro shows both as downloaded without any file moving; fp16 and q4f16 Turbo groups appear with correct sizes; killing the process mid-download and restarting resumes.

### Phase 5 - Turbo on GPU, expressive routing, advisor, legacy removal

1. Rewrite the Turbo LM decode loop around `OrtIoBinding` with device-resident KV cache if S2 shows the copy cost matters; otherwise implement split placement (encoder and decoder on GPU, LM on CPU) through `OnnxSessionFactory` roles. Both paths behind the same `ChatterboxSynthesizer.CreateTurbo(plan)`.
2. `ExpressiveRouter`: take the expressive engine id and device from config (F2), keep the per-line fallback to the primary on any failure, add GPU retry/status.
3. Allow Turbo as primary; `engineKey` for the render cache includes engine id + precision + device class (not adapter) so a CPU/GPU switch does not serve stale audio of a different engine.
4. `Domain/SetupAdvisor` (hardware -> engine, precision, downloads, bytes) used by the wizard.
5. Delete the legacy Chatterbox variant, `UseFp32LanguageModel` consumers, the q4 asset group (keep file deletion as a user action), and the `ChatterboxSynthesizerReadyTests` cases that cover it.
Tests: `SetupAdvisorTests` (table), `ExpressiveRouterTests` (engine from config, fallback on GPU failure, retry), `ChatterboxTurboSynthesizerTests` updates (session role placement with fakes), render-cache key test.
Acceptance (needs the owner's GPU): Turbo fp16 on the RX 9070 XT under Wine reaches RTF below 1.0 for a 15-word line, first audio of a streamed 3-sentence line under 2 s after warm-up; GPU impact `gentle` keeps game frame time p99 within an agreed budget (measure with MangoHud during synthesis; budget set after S4); CPU path unchanged.

**Status (ticket #13, 2026-10-17).** Implemented as written, with the S2 variant (LM on the GPU, device-resident cache):

- `TurboDecodeLoop` (OrtValue + OrtIoBinding, Float16 or Float cache read from the graph) serves GPU and CPU; `TurboModelSet` resolves precision (`auto`: q4f16, then fp16, on a GPU plan; fp32 on the CPU) and `ChatterboxSynthesizer` plans per precision (the allowlist keys on the chosen LM graph). The crash scope closes after the first decoded line; a managed GPU failure rebuilds on the CPU (`FellBack`, retry offered) and the line still finishes.
- `ExpressiveRouter` takes the engine from `Engine.Expressive.EngineId` (container resolves it through the catalog), device and precision from `Device.Mode` and `Engine.PerEngine[id]`; a failed expressive line sits the engine out for 60 s or until Retry.
- The render-cache key is `engineId:precision/deviceClass` (plus the expressive engine's variant while routing is on).
- `Domain/SetupAdvisor` drives the Home setup flow: with a verified GPU, Balanced and Most expressive ask for the `turbo` base group plus `turbo-q4f16`.
- Legacy Chatterbox and F5 code, the F5 package and the F5 SmokeSynth path are gone; their catalog groups stay flagged Retired. The eight reference clips moved from `voices-f5/` to `voices-clips/` (`ReferenceClipBank`) and still back Turbo-as-main when a voice has no clip of its own.
- Measured on the RX 9070 XT (shared, heavily loaded machine; see the ticket for numbers): the GPU stream equals the CPU stream for short lines and diverges later on near-ties; the CPU decoder is the bottleneck.

### Phase 6 - Remote engine

`Infrastructure/Net/RemoteSynthesizer.cs` (loopback by default; non-loopback needs an explicit "I understand" flag in `EngineOptions.Extra`), protocol = OpenAI-compatible `POST /v1/audio/speech` returning wav, with a documented extension for reference clips (`voice` = id, optional `reference_wav` base64). `tools/aiva-server/` (Python, optional, not shipped) with Kokoro + Chatterbox backends for CUDA/ROCm. Engine descriptor `remote`, status reports latency and errors.
Tests: `RemoteSynthesizerTests` with a stub `HttpMessageHandler` (success, timeout, 5xx, bad wav, cancellation), loopback guard.
Acceptance: a Linux user can point the plugin at `http://127.0.0.1:PORT` and speak lines without the plugin touching any GPU API.

### Phase 7 - Follow-ups (own plans)

Pocket TTS and Multilingual engines; decide DirectML removal plus ORT bump; downloadable pack index + zip install; F5 removal decision; llama.cpp Vulkan runtime for Orpheus-class models.

## 7. Spikes, in order (phase 0)

Record results in `docs/plans/2026-10-gpu-spike-results.md` (one section per spike: command, numbers, decision).

| # | Spike | How | Time-box | Go / no-go |
|---|---|---|---|---|
| S0 | WebGPU EP on native Linux, RX 9070 XT | In a scratch copy of `tools/SmokeSynth`: pin core ORT to 1.24.4, add the WebGPU plugin package (linux-x64), register by path, append EP with `Vulkan`, load KokoroSharp's `kokoro-v1.0.onnx` and the Turbo graphs. Measure: session creation succeeds, ORT verbose log for nodes left on CPU, RTF vs CPU (Kokoro RTF 0.5 baseline), first-run shader compile time, option key spelling (prefixed or not). Also time the iGPU. | 1 day | Kokoro: keep CPU default unless GPU is at least 3x faster on first-sentence latency. Turbo: need a working encoder and decoder on GPU. |
| S1 | Same binary under the XIVLauncher Wine | `dotnet publish -r win-x64 --self-contained`, run with the `wine-xiv-staging` binary against `~/.xlcore/wineprefix`. Compare adapter list, outputs (waveform correlation with S0), timings. Also capture the current DirectML failure text for the record. | 1 day | If Dawn finds no Vulkan adapter or crashes: promote phase 6 (Remote) to the primary Linux GPU story and file the Dawn/Wine issue; plan continues for native Windows. |
| S2 | Turbo decode loop cost on GPU | Measure per-token time with today's managed KV round trip vs `OrtIoBinding` device buffers (is a WebGPU buffer bindable from the managed API at all?) vs LM-on-CPU split. Use fp16 and q4f16. | 1-2 days | Pick phase 5 step 1 variant. |
| S3 | Custom voices in KokoroSharp | Load an existing KVoiceWalk `.npy` (the Irish one in `models/kvoicewalk-work`, which already synthesizes) through `LoadVoicesFromPath` under three names (`am_x`, `bm_x`, neutral); determine how the language/G2P is chosen; A/B American vs British phonemization for an Irish voice. | half day | Decides the internal-name mapping in phase 2 step 3. |
| S4 | In-game dev build behind a hidden toggle | Phase 3 skeleton in the dev plugin: session creation inside Dalamud's ALC, crash behavior, frame time while synthesizing (MangoHud), dGPU vs iGPU. | 1-2 days | Sets the `GpuImpact` presets and the frame-time budget. |
| S5 | Pocket TTS ONNX and VCTK license check | Evaluate one community ONNX for quality and RTF; verify VCTK v0.92 license on DataShare. | half day | Gates phase 7 items and the Australian/Indian pack. |

## 8. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Dawn Vulkan under Wine fails or crashes the game | Primary Wine GPU path dead | S1 before any plugin work; crash guard; Remote engine as the Linux fallback; CPU always works. |
| WebGPU EP lacks operators for Kokoro/Turbo graphs (CPU fallback nodes add copies) | GPU slower than CPU | S0 counts CPU-assigned nodes; per-session device roles; Kokoro stays CPU by default. |
| KV-cache copies dominate Turbo on GPU (F3) | No speedup | S2; IO binding or split placement. |
| GPU work stutters the game (shared GPU, no priority control) | Players disable the plugin | `GpuImpact` pacing, `maxNumPendingDispatches`, iGPU option, frame-time budget in acceptance. |
| Plugin EP package is 0.4.0 (young) | API/option changes | Pin exact version; keep DirectML fallback on native Windows for one release; wrap options in `WebGpuOptions`. |
| VRAM: Turbo fp16 1.66 GB on top of the game | OOM on 6-8 GB cards | q4f16 option (0.56 GB), advisor chooses; catch allocation failure -> `FellBack`. |
| Accent voices sound weak or off (KVoiceWalk similarity about 0.55-0.6, American G2P) | Pack disappoints | `quality: experimental` labels, human listening gate, British G2P test (S3), reference clips + Turbo as the high-fidelity path. |
| CC-BY-SA obligations on derived voices and clips | Legal | Separate pack with LICENSE/NOTICE, attribution surfaced in UI, owner confirms SA interpretation before release. |
| Config migration loses a setting | Angry users | Typed migration, fixtures from real files, backup file, both serializers tested, flat properties untouched. |
| ORT version lock (DirectML pins 1.24.4) | Misses newer WebGPU fixes | Phase 7 decision; do not mix ORT natives (csproj comment already warns). |
| Concurrent edits with the UI plan in `ConfigurationWindow.cs` | Merge pain | Phase 1 lands first, property-rename-only edits there. |

## 9. Test and verification notes

Tests compile Dalamud-free sources (see `AIVoiceActing.Tests.csproj`); every new Infrastructure class that needs unit tests must live under a path the glob already compiles (`Infrastructure/Onnx`, `Kokoro`, `Storage`, `Net/HuggingFaceProvisioner.cs`) or be added to that csproj explicitly. New Domain/Ports files are picked up automatically and are covered by `ArchitectureScanTests`. GPU-dependent tests are gated by `AIVA_GPU_TESTS=1` and are never required in CI. Build/test commands stay as in `AGENTS.md`; update `AGENTS.md` (layout, new `tools/voicepack/`, WebGPU runtime note) in the phase that adds them.

## 10. Contract for the UI plan

Names align with section 9 of `2026-10-ui-redesign.md` where possible. Differences are marked **(changed)**.

### 10.1 Settings the UI must expose (all in `Configuration` v2)

| Setting | Location | Control and rules |
|---|---|---|
| Engine | `Engine.Primary` | Engine cards from `IEngineCatalog` (Fast/Slower/Slow badges, license line, NonCommercial warning for F5, size per precision). Changing it invalidates the synthesizer as today. Aliases never shown. |
| Expressive voices | `Engine.Expressive.Enabled` (+ `EngineId` under Advanced) | Toggle; disabled with reason when Turbo is not installed; shows the GPU/CPU expectation. Replaces the old `ExpressiveEngine` bool. |
| Run on | `Device.Mode` | Segmented Automatic / GPU / CPU **(changed: field is `Device`, not `DevicePreference`)**. "Automatic" means each engine's recommended device (Kokoro CPU, Turbo GPU). |
| Adapter | `Device.AdapterId` | Picker over `DeviceReport.Adapters` (all adapters, including the iGPU); hidden unless an adapter exists. |
| GPU load | `Device.GpuImpact` | gentle / balanced / fast with one-line explanations (frame rate vs speed). |
| CPU load | `Device.CpuImpact` | low / medium / high, moved from the top level. |
| Advanced device | `Device.Backend`, `Device.Api`, `Device.FallbackToCpu` | Only under "Show advanced details". |
| Per-engine overrides | `Engine.PerEngine[id].Device/Precision/Extra` | Advanced: precision picker (auto, fp16, q4f16, fp32) with download sizes; Remote engine URL under `Extra["url"]`. |
| Voice packs | `Voices.DisabledPacks`, `Voices.ExtraPackDirs` | Per-pack enable switch, "Open packs folder", rescan button (calls `IVoiceLibrary.Reload()`). |
| Unchanged settings | flat props listed in 5.1 | No renames. |
| UI-owned fields | `ShowAdvanced`, `ReduceMotion`, `LastPage`, `OnboardingCompletedVersion`, `OnboardingStep`, `MigrationNoticeAckedVersion` | Declared in this schema so the version has one owner; defaults in 5.1. The UI plan implements behaviour. |
| Removed | `SelectedEngine`, `SelectedEp`, top-level `CpuImpact`, `ExpressiveEngine`, `UseFp32LanguageModel` | UI must not read or write them (nullable and obsolete). |

### 10.2 State the UI reads (polled per frame, immutable snapshots)

| Port member | Shape | Use |
|---|---|---|
| `IEngineCatalog.List()` | `EngineInfo` (4.1) | Engine cards, download tables, wizard. Group and asset keys come from here; removes the `Infrastructure.Onnx.ModelCatalog` reference at `ConfigurationWindow.cs:316-317`. |
| `IDeviceProbe.Detect()` / `.LastReport` | `DeviceReport` (4.1) | Detected-hardware card, wizard recommendation, Wine note ("GPU through Vulkan"). Cached; `Refresh()` runs the self-test on a background task. |
| `ISpeechSynthesizer.Status` | `EngineStatus` | Footer pill, Overview headline, Engine card state table (NotInstalled / Loading / Ready / FellBack / Failed); `Reason` is final user text, the UI must not invent fallback messages. |
| `ISpeechSynthesizer.RetryAsync(DeviceMode, ct)` | Task | "Retry on GPU", "Use CPU". After a crash-guard trip, "Retry on GPU" clears `Device.GpuDisabledByCrashGuard`. |
| `IVoiceLibrary` | `Voices` (VoiceInfo), `Packs` (VoicePackInfo: Id, Name, Version, License, Bytes, VoiceCount, Enabled, Problems[]), `Find(id)`, `Has(id)`, `Reload()` | Voice browser, pickers, "Missing voice" rows (unresolved ids). `VoiceInfo` is a superset of the UI plan's assumption: adds `Tags`, `Quality` (stock / verified / experimental), `PreviewFile?`, `Realizations` kinds. |
| `IConfigMigrationReport` | `FromVersion, ToVersion, Changes[{Area, Before, After, Message}]` | Backed by `Configuration.Migration` and persisted until acknowledged **(changed: persisted, so a restart before reading does not lose the banner)**. Ack action: clear `Migration.Changes`, set `MigrationNoticeAckedVersion = CurrentVersion`. |
| `IModelProvisioner` | existing + `FreeBytes(dir)` and resume | Download queue: bytes, speed, resume, free-disk check before queueing. |

### 10.3 Progress and events (no push events; poll snapshots)

| Flow | Source | Fields the UI shows |
|---|---|---|
| Model download | `IProgress<DownloadProgress>` per asset (existing) | file k of n, bytes, total, cancel, retry, resumed flag. |
| Engine load / warm-up | `EngineStatus.State = Loading` plus `Detail` ("Loading Kokoro on GPU...", "Compiling GPU shaders, first line only") | Indeterminate bar and text. First GPU line can take seconds (shader compile); the UI must not read that as a hang. |
| Device self-test | `DeviceReport.SelfTest` (`Running/Passed(ms)/Failed(reason)`) | Spinner, result line, "Copy details". |
| Fallback | `EngineStatus.State = FellBack`, `RequestedDevice`, `ActiveDevice`, `Reason`, `CanRetryGpu` | Banner with Retry on GPU / Keep CPU / Copy details. |
| Performance | `EngineStatus.LastRtf`, `LinesRendered` | Optional "about 3x real time" hint in the Engine card and wizard benchmark step. |
| Voice pack scan | `IVoiceLibrary.Packs[].Problems` | Per-pack warning list ("2 voices unavailable: file missing"). |
| Crash guard | `Device.GpuDisabledByCrashGuard` plus a pending `Migration.Changes` style notice | Banner "GPU was turned off after a crash" with Retry. |

### 10.4 Answers to the UI plan's alignment checklist

1. Names: adopted `IEngineCatalog`, `EngineInfo`, `DeviceReport`, `EngineStatus` (+`RetryAsync`), `IVoiceLibrary`, `VoiceInfo`, `VoicePackInfo`, `IConfigMigrationReport`; config section is `Device` rather than `DevicePreference`.
2. Migration report: written by `ConfigMigrator` at plugin load, persisted in `Configuration.Migration` until acknowledged.
3. Free disk and resume: both added to `IModelProvisioner` in phase 4.
4. Per-voice preview line: packs may ship `previewFile` (3 s wav); stock voices get previews generated by the tool and shipped (about 3 MB). The library exposes `PreviewFile`; without it the UI previews through the engine as planned.
5. Collisions: the UI-owned additive fields are included in 5.1; schema bump is 1 -> 2 once, owned here.
6. Voice renames: none. Stock Kokoro ids are unchanged; ids can only go missing when a pack is removed, which the library reports as unresolved so the UI shows its "Missing voice" row.
7. Phase timing: UI P4 (Engine/Device) needs phases 1 and 3 ports; UI P5 (Voices) needs phase 2; UI P6 (wizard) needs `SetupAdvisor` from phase 5 or its simple Phase-3 precursor (`DeviceModePolicy`). Until then the UI adapters in its section 9 are fine.

## 11. Open questions for the owner

1. Confirm the CC-BY-SA-4.0 reading for bundling derived style tensors and clips in the plugin zip (separate-collection approach assumed).
2. Is a 156 MB zip plus about 14 MB for WebGPU acceptable for your distribution channel, or should non-English voices move to downloadable packs in phase 2?
3. After S0: should Kokoro default to GPU on machines where it wins, or stay CPU to keep VRAM and frame time free for the game? (Plan defaults to CPU.)
4. F5-TTS: keep in Labs or remove to drop `Horus.F5Tts.Onnx`?
