# GPU spike results (issue #8)

Date: 2026-10-17. Spikes S0 to S3 from `docs/plans/2026-10-gpu-engines-and-voices.md` section 7. Research only, no plugin code changed. Harness: `tools/spikes/webgpu/` (README there). Raw logs, models and publish output stay under the gitignored `models/spike-work/`.

Machine: CachyOS, Ryzen 7 9800X3D (16 threads) with its Raphael iGPU (0x13c0), Radeon RX 9070 XT (0x7550), Mesa RADV (Vulkan 1.4.354), no NVIDIA or ROCm. Wine is the XIVLauncher managed build `wine-xiv-staging-fsync-git-10.8.r0.a2ca9e4` against `~/.xlcore/wineprefix`. Other agents were using the CPU and GPU during the runs, so absolute CPU timings are noisy (plus or minus 30%); the GPU-versus-CPU ratios are large enough not to care.

## Summary and decisions

| Question | Answer |
|---|---|
| Does the WebGPU plugin EP work on Linux (Vulkan) and under the XIVLauncher Wine? | Yes for graphs it can run. Same behaviour, same token output, same speed within noise (section S1). Needs two Wine-only workarounds (backend and `vulkan-1.dll`). |
| Option spelling | `dawnBackendType` **without** the `ep.webgpuexecutionprovider.` prefix. The prefixed form is silently ignored. |
| Kokoro on GPU | **No-go.** LSTM kernel segfaults at session creation, then every other attempt aborts the process at the first `Run`. CPU is fast anyway (RTF 0.12 to 0.22). Keep Kokoro on CPU. |
| Turbo on GPU | **Conditional go, language model only.** With `Microsoft.ML.OnnxRuntime.EP.WebGpu` **0.2.1** the LM runs on the GPU (0.1.0 not tested). EP 0.3.0 and 0.4.0 abort the process on the LM, the conditional decoder, and anything with a CPU/GPU memory copy node. Encoder, embedding and decoder stay on CPU. |
| Managed KV round trip vs device-resident KV | Device-resident (`OrtIoBinding`) is usable from the managed API and is 3 times faster at 250 tokens (12 ms vs 37 ms per token, fp16). Mandatory. |
| LM on CPU as the split option | Not worth it: 100 to 117 ms per token, no better than today. |
| Custom Kokoro voice language | Decided purely by the voice **name**: character 0 picks the G2P, character 1 is only a gender label. Rule in section S3. |
| DirectML under Wine | Fails at provider creation, HRESULT `0x887A0002`. |

Biggest finding for ticket #12: the failure mode is a **native process abort** (SIGABRT, or `0xC0000005` under Wine), not a managed exception. Inside Dalamud that kills the game. A small self-test graph passes while the real graph aborts, so the plan's in-process `DeviceProbe.SelfTest` cannot protect anyone; the crash marker and a pinned, hash-checked list of known-good (EP version, graph) pairs have to do that job.

## S0: WebGPU EP on native Linux

### Setup

Scratch app in `models/spike-work/webgpu` (committed copy: `tools/spikes/webgpu`). `Microsoft.ML.OnnxRuntime` 1.24.4, `Microsoft.ML.OnnxRuntime.EP.WebGpu` 0.4.0 first, then 0.3.0, 0.2.1 (all three list "ORT 1.24.4 or later"). The EP library is registered by absolute path.

```bash
dotnet build tools/spikes/webgpu/Spike.csproj -c Release -p:EpVer=0.4.0
dotnet Spike.dll enum
dotnet Spike.dll kokoro model=kokoro.onnx voice=irish.npy device=webgpu dev=1 opts=dawnBackendType:Vulkan ntok=60 runs=6
dotnet Spike.dll turbo dir=hf/onnx prec=q4f16 all=cpu lm=webgpu kv=iobind dev=1 opts=dawnBackendType:Vulkan steps=60
```

### Registration and adapters

| Item | Result |
|---|---|
| `RegisterExecutionProviderLibrary` | 6 to 13 ms |
| `GetEpDevices` | 0 to 1 ms. Three devices: CPU, WebGPU iGPU (`did 0x13c0`, `card_idx=0`), WebGPU 9070 XT (`did 0x7550`, `card_idx=1`). Linux metadata is only PCI ids and bus id, no adapter name. |
| Native `libonnxruntime_providers_webgpu.so` | 10.4 MB (0.2.1), 15.8 MB (0.3.0), 16.4 MB (0.4.0); needs only `libvulkan.so.1` |
| Which adapter is used | **The `OrtEpDevice` passed to `AppendExecutionProvider` is ignored.** Index 0 and index 1 both ran on the 9070 XT. Measured with `/sys/class/drm/card*/device/gpu_busy_percent` while decoding: `powerPreference=high-performance` (also the default) loads card1 (9070 XT) 99 percent and card0 0 percent; `powerPreference=low-power` loads the iGPU 100 percent and the 9070 XT 6 percent. There is no `adapterIndex` string in the 0.2.1, 0.3.0 or 0.4.0 binaries. `deviceId` is parsed ("WebGPU EP Device ID: 0" in the verbose log); I did not test whether it selects an adapter. |

So the plan's `Device.AdapterId` picker cannot choose a specific adapter with these EP builds. It can choose a class: performance (discrete) or low power (integrated). With two discrete GPUs the choice is Dawn's.

### Option keys (answers F8)

Probed with deliberately bad values on a small graph (`embed_tokens_fp16.onnx`):

| Probe | Result |
|---|---|
| `dawnBackendType=D3D12` on Linux | Fails: "No supported adapters". The option is honoured. |
| `dawnBackendType=Bogus` | Fails: `Invalid Dawn backend type: Bogus`. Honoured. |
| `ep.webgpuexecutionprovider.dawnBackendType=D3D12` or `=Bogus` | Session loads fine. **Silently ignored.** |
| `bogusKey=1`, `ep.webgpuexecutionprovider.bogusKey=1` | Both ignored, no error. A typo in a key never errors. |
| `powerPreference` | `high-performance` or `low-power`. `HighPerformance` fails with `Invalid power preference`. |
| `enableInt64` | `1` or `0`. `true` fails with `Invalid enableInt64 value: true`. |
| `forceCpuNodeNames` | Newline separated node names, hard cap 8192 characters per value. Useful for bisecting. |

Use the unprefixed form in `WebGpuOptions.Build`, and treat a wrong key as invisible, so add a unit test that pins the exact key strings.

### Kokoro (`kokoro-v1.0.onnx`)

CPU baseline with the same harness (60 tokens, 4.3 s of audio, 16 threads): session creation 436 to 805 ms, steady 0.51 to 1.0 s per call, **RTF 0.12 (8 threads) to 0.23**; 30 tokens RTF 0.22; 150 tokens RTF 0.19.

GPU, ORT 1.24.4 plus EP 0.4.0, Vulkan, dGPU:

| Step | Result |
|---|---|
| Session creation | **Segmentation fault** (native, uncatchable) after "Done saving initialized tensors". Bisected with `forceCpuNodeNames` to the 6 WebGPU `LSTM` nodes. Forcing them to CPU lets the session finish (490 to 688 ms). |
| First `Run` after that | **Abort**: `OrtDevice(...) memory_type == MemType::DEFAULT || ... was false. Invalid memory type: -1`. Same with the `If`, `Resize`, `ConvTranspose`, `CumSum`, `Pad`, `Shape` nodes also forced to CPU. |
| ORT core 1.29.0 plus EP 0.4.0 | Session creation succeeds (721 ms, no LSTM crash) and the first `Run` aborts with the same message. |
| EP 0.2.1 | Same segfault at session creation. |

Node placement from the verbose log (EP 0.4.0): 1816 nodes on WebGPU, 436 on CPU, and 64 `MemcpyFromHost`/`MemcpyToHost` nodes in the listing. CPU-only ops: `Loop`, `SequenceAt/Empty/Insert`, `SplitToSequence`, `ConcatFromSequence`, `TopK`, `ScatterElements`, `GatherElements`, `Round`, `ReduceProd`, `ReduceMax`, `Greater/GreaterOrEqual`, `RandomNormalLike`, `RandomUniformLike`, `Cast`, plus the shape-chain ops. This graph (the dynamic-length duration predictor and the noise sources) is a poor fit for the EP even without the crashes.

**Decision: no-go, Kokoro stays on CPU** (the plan's default). The "3 times faster first-sentence latency" bar is moot: first sentence on CPU is about 0.5 s.

### Turbo graphs (official `ResembleAI/chatterbox-turbo-ONNX`, fp16 and q4f16)

Downloaded with curl into `models/spike-work/hf/onnx/` (about 3 GB). Session creation and placement per graph, EP 0.4.0, Vulkan, 9070 XT:

| Graph (fp16) | `enableInt64` off | `enableInt64=1` | Loads | First run |
|---|---|---|---|---|
| `speech_encoder` | **Fails to load**: no WebGPU kernel for `Cast(19)` on int64 (`/resampler/Cast_1_output_cast0`) | 3747 nodes WebGPU, 74 CPU, 579 memcpy nodes | 0.9 to 1.2 s | works, same 151 frames and 186 tokens as CPU |
| `embed_tokens` | 6 WebGPU, 4 CPU | 8 WebGPU, 2 CPU | 40 to 147 ms | works |
| `language_model` | 440 WebGPU, 6 CPU | 447 WebGPU, 1 CPU | 170 to 280 ms | **aborts** (see below) |
| `conditional_decoder` | 4310 WebGPU, 1488 CPU | 6385 WebGPU, 87 CPU (STFT, `RandomNormalLike`, `RandomUniformLike`, `Tile`, ...), 724 memcpy nodes | 2.0 to 2.7 s | **aborts** |

q4f16 behaves the same (the encoder needs `enableInt64=1` too; the other three load).

Run time, EP 0.4.0:

* Encoder on GPU: first run 461 to 799 ms, steady 311 to 317 ms (CPU 431 to 640 ms). It is run once per reference clip and cached, so not worth a GPU dependency.
* LM: **abort** with the same "Invalid memory type: -1" message on the first `Run`, in fp16, with attention-mask preprocessing rewritten away (graph surgery: `total_sequence_length` made a graph input, zero CPU nodes, zero memcpy nodes, still aborts) and in a one-layer cut (`lm_gqa`: `GroupQueryAttention` alone). The WebGPU `GroupQueryAttention` kernel is the trigger in 0.3.0 and 0.4.0.
* Conditional decoder: abort.

### Minimal repros of the abort

`tools/spikes/webgpu/repro/genrepro.py` writes three single-purpose graphs:

| Graph | Result on EP 0.4.0 (and 0.3.0, 0.2.1) |
|---|---|
| `rangef.onnx`: one float `Range` | Abort. Int64 `Range` aborts only when `enableInt64=1` routes it to the WebGPU kernel. |
| `chain.onnx`: `Add -> Mul -> Add`, run normally | Works. |
| same, with `forceCpuNodeNames=mul1` so ORT inserts `MemcpyToHost` and `MemcpyFromHost` around a CPU `Mul` | Abort. |
| Add, Sub, MatMul, Conv, Gather, Transpose, Concat, Where, Slice, LayerNormalization, Gelu, Cast, ReduceSum, Reshape(Shape), fp32 and fp16 | All run. |
| `GroupQueryAttention` (0.2.1 only) | Runs; 0.3.0 and 0.4.0 abort. |

The error text is thrown in `ortdevice.h:77`. `-1` is `OrtMemTypeCPUOutput`, so my reading is that the plugin wrapper converts a kernel's CPU-output memory type to an `OrtDevice` without mapping it. That is a guess from the symptom; the binaries are stripped and I did not confirm it. The same abort happens with core ORT 1.24.4, 1.29.0, 1.30.0 and 1.31.0 against EP 0.4.0, so it is in the EP library, not the core. The encoder has 579 memcpy nodes and works, so memcpy alone is not the trigger; the exact condition is unknown. Worth filing upstream with these repros (microsoft/onnxruntime, WebGPU plugin EP).

### EP version matrix

| Check | 0.2.1 | 0.3.0 | 0.4.0 |
|---|---|---|---|
| Add/MatMul/embedding graphs | ok | ok | ok |
| `chain` with CPU node in the middle | abort | abort | abort |
| One-layer `GroupQueryAttention` | **ok** | abort | abort |
| Full Turbo LM, fp16, fp32 and q4f16 | **ok** | abort | abort |
| Speech encoder | runs but **wrong output** (104 frames, 103 prompt tokens, CPU gives 151 and 186) | not tested | ok with `enableInt64=1` |
| Conditional decoder | abort | not tested | abort |
| Kokoro | LSTM segfault | not tested | LSTM segfault |

ORT core 1.31.0 with EP 0.2.1 also works for the LM (q4f16 3.0 ms per token, faster than 4.8 ms on 1.24.4), so the "keep ORT at 1.24.4" constraint comes from the DirectML package, not from WebGPU.

### Shader compile and first run

Dawn compiles pipelines lazily. Measured in the same process (RADV disk cache warm, so a cold first-ever run could be slower):

| Graph | First run | Steady |
|---|---|---|
| Embed | 9 to 16 ms | 0.2 to 0.6 ms |
| Encoder (0.4.0) | 461 to 799 ms | 311 to 317 ms |
| LM prefill (151 + 24 frames) q4f16 | 38 to 44 ms | n/a |
| LM first decode step, q4f16, iobind | 15 to 25 ms | 3 to 5 ms |

Session creation for the LM is 100 to 380 ms depending on precision; most cost is weight upload. Nothing here needs a "compiling shaders" UX, but warm up the LM during engine load as the Kokoro path already does.

## S1: same binary under the XIVLauncher Wine

### Commands

```bash
dotnet publish tools/spikes/webgpu/Spike.csproj -c Release -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:UseAppHost=true -p:EpVer=0.4.0 -o publish-win   # 121 MB
WINEPREFIX=~/.xlcore/wineprefix WINEFSYNC=1 \
  ~/.xlcore/compatibilitytool/wine/wine-xiv-staging-fsync-git-10.8.r0.a2ca9e4/bin/wine publish-win/Spike.exe enum
```

### Results

| Item | Result |
|---|---|
| Adapters via the bare prefix environment | One device: `ATI Radeon HD 5600 Series`, `did 0x68d8`, 16 GB. That is Wine's built-in wined3d fake adapter, not a real GPU. |
| Adapters with the launcher's `WINEDLLOVERRIDES=d3d9,d3d11,d3d10core,dxgi=n,b` (DXVK `dxgi`, as in the game process) | Two devices with real names: `AMD Radeon RX 9070 XT (RADV GFX1201)` (`did 0x7550`, 16304 MB, `DxgiHighPerformanceIndex=0`) and `AMD Ryzen 7 9800X3D ... (RADV RAPHAEL_MENDOCINO)` (`did 0x13c0`, 512 MB, index 1). Inside Dalamud the list will be the real one. |
| Default backend (no `dawnBackendType`) | **Fails**: `Failed to get a WebGPU device: D3D12 create fence handle failed with E_NOTIMPL (0x80004001)`. Dawn picks D3D12 on Windows, Wine's D3D12 is not enough. Forcing Vulkan is mandatory under Wine. `dawnBackendType=D3D12` fails identically. |
| `dawnBackendType=Vulkan`, stock layout | **Fails**: `Warning: Couldn't load Vulkan: DynamicLib.Open: vulkan-1.dll Windows Error: 87`, then "No supported adapters". Dawn probes `<EP dll directory>\vulkan-1.dll`, then `<exe directory>\vulkan-1.dll` by full path and nowhere else. `LoadLibraryExA("vulkan-1.dll")` itself succeeds from System32 for every flag combination I tried, so the Wine loader is fine; Dawn just does not use the search path. |
| Same, with the prefix's `C:\windows\system32\vulkan-1.dll` (57 KB Wine PE stub) copied next to the EP dll | **Works.** The Dawn-to-winevulkan-to-RADV path runs. |
| Which GPU Dawn uses under Wine | Same rule as native: default and `high-performance` use the 9070 XT (card1 76 percent busy, iGPU 0), `low-power` uses the iGPU (100 percent, 9070 XT 11 percent). |
| Encoder on GPU, EP 0.4.0 + `enableInt64=1` | 151 frames, 186 prompt tokens (identical to native), first run 553 ms, steady 374 ms. |
| Turbo LM on GPU, EP 0.2.1, q4f16, iobind | Tokens **identical** to the native run (`79,305,2573,4850,...`), 5.1 to 6.3 ms per token, prefill 38 ms. fp16 iobind 13.7 ms, fp16 managed 30.2 ms. iGPU q4f16 16.6 ms. |
| Kokoro CPU under Wine | RTF 0.147 (native 0.215 in the same period); waveform Pearson 0.993 versus native. Not 1.0 because the graph has random-noise nodes (identical inputs vary by a few hundred samples even between native runs). |
| Kokoro GPU under Wine | `0xC0000005` at session creation, as native. |
| Turbo decoder GPU under Wine | Aborts as native. |
| DirectML (`Microsoft.ML.OnnxRuntime.DirectML` 1.24.4, as shipped) | `providers: DmlExecutionProvider,CPUExecutionProvider` is listed, then `AppendExecutionProvider_DML(0)` throws `OnnxRuntimeException ... dml_provider_factory.cc(549) ... Exception(1) tid(24) 887A0002`. 0x887A0002 is `DXGI_ERROR_NOT_FOUND`: no D3D12 adapter. Expected failure confirmed. |

### Side effect on the user's prefix

My first Wine run used `wine-xiv-staging-fsync-git-10.8.r0.g47f77594`, which is **not** the build the launcher last downloaded (`a2ca9e4`, 2026-04-17). Wine updated `~/.xlcore/wineprefix` for the build change (about 1600 built-in files and the three `.reg` files rewritten, `.update-timestamp` touched). All later runs used `a2ca9e4`, which brought the built-ins back to the launcher's own build. The DXVK files (`d3d9/d3d10core/d3d11/dxgi.dll`, dated 2026-05-29) were not touched. This is what XIVLauncher does itself on a build change, but it is a change I made to a live prefix. Everything else (scratch DLL copies) stayed under `models/spike-work/`. No game process was running. I expect no action is needed; if the next launcher start looks off, this is the first thing to check.

### Go / no-go

* The plan's A option (WebGPU plugin EP, Dawn Vulkan) is **go for loading and running EP-compatible graphs under Wine**, with the two workarounds above. D3D12 backend under Wine is no-go.
* The abort bugs in EP 0.3.0 and 0.4.0 are independent of Wine (identical natively), so they are not a reason to promote the Remote engine, but they are a reason to pin EP 0.2.1 for Turbo.
* Native Windows with the D3D12 backend was **not tested** (no Windows machine). The plan's A' assumption stays unverified; expect the same EP-library aborts there.

## S2: Turbo decode loop cost

Harness `turbo` mode. Loop is a faithful port of `ChatterboxSynthesizer.SynthesizeCore` (embed, LM with KV cache, repetition penalty 1.2, argmax) using fixed text ids and STOP masked so that every run decodes exactly N steps. Reference clip is the bundled 7.4 s `turbo-default-voice.wav` (151 condition frames, 186 prompt tokens). Encoder, embed and decoder on CPU, only the LM varies. 9070 XT unless stated. ms per decoded token, steady state (first 3 steps skipped).

| LM placement | KV handling | fp32 | fp16 | q4f16 |
|---|---|---|---|---|
| CPU | managed (today) | 118 | 100 (60 steps), 117 (250 steps) | 59 |
| WebGPU (EP 0.2.1) | managed round trip | 45 | 31 (60), **37 (250)** | 25 |
| WebGPU (EP 0.2.1) | **`OrtIoBinding`, device resident** | 13.6 | **12.4 (60), 12.1 (250)** | **4.8** |
| WebGPU iGPU (EP 0.2.1) | managed | n/a | n/a | 38 |
| WebGPU iGPU (EP 0.2.1) | iobind | n/a | 29 | 15.5 |
| Same, ORT 1.31.0 | iobind | n/a | n/a | 3.0 |
| Under Wine, dGPU | iobind | n/a | 13.7 | 5.1 to 6.3 |

Prefill (175 frames): 38 to 72 ms on GPU versus 379 to 531 ms on CPU.

Observations:

* Managed round trip costs 2.5 to 5 times more than device-resident KV, and it grows with context (31 to 37 ms from 60 to 250 steps; iobind stays flat). The KV cache is 24 layers x 2 x 16 heads x 64 x 2 bytes per position (about 98 KB per token in fp16).
* LM on CPU with GPU for the rest is moot: the rest cannot run on the GPU (section S0).
* Greedy tokens from the fp16 GPU LM equal the CPU run for the first 7 tokens and then diverge (fp16 near-ties); q4f16 iobind and q4f16 managed produce the identical sequence, so iobind is numerically sound. Whether the audio differs audibly needs a listening test with real text (this harness feeds fixed ids, so the decoder output is not meaningful speech).
* **The conditional decoder on CPU is now the limit**: about 1.0 to 1.9 s for 2.4 s of new audio and 3.3 to 3.8 s for 10 s of new audio. With a q4f16 GPU LM the end-to-end estimate for 10 s of audio is about 1.2 s (LM) plus 3.3 s (decoder) = RTF 0.45; fp16 about 0.64; all-CPU fp16 is 3.3. Trimming the reference clip from 7.4 s to 3 s did not give a measurable decoder speed-up (1259 ms vs 1306 ms, within noise).

### Is a device buffer bindable from the managed API?

Yes, on EP 0.2.1 (and the API exists on 0.4.0, but there the LM aborts before it matters). Working recipe, as implemented in `Turbo.Loop`:

```csharp
var devInfo = lmEpDevice.GetMemoryInfo(OrtDeviceMemoryType.DEFAULT);   // name "WebGPU_Buf", DeviceAllocator, vendor 0
using var bind = session.CreateIoBinding();
bind.BindInput("inputs_embeds", cpuOrtValue);                          // ORT copies host inputs itself
bind.BindInput("past_key_values.N.key", previousStepPresentOrtValue);  // device resident, no copy
bind.BindOutputToDevice("present.N.key", devInfo);                     // 48 outputs, ORT allocates on the GPU
bind.BindOutputToDevice("logits", OrtMemoryInfo.DefaultInstance);      // host, only the last row is read
session.RunWithBinding(runOptions, bind);
bind.SynchronizeBoundOutputs();
var outs = bind.GetOutputValues();                                     // keep alive until the NEXT step finished, then dispose the previous set
```

The first step binds zero-length CPU tensors (`[1,16,0,64]`) as `past`. For fp16 and q4f16 graphs the KV tensors are `Float16` (`Microsoft.ML.OnnxRuntime.Float16`), so the current `DenseTensor<float>` KV code cannot be reused; the loop must be OrtValue based. `inputs_embeds` stays float32 in all variants.

## S3: custom voices in KokoroSharp

KokoroSharp 0.8.4, decompiled with `ilspycmd` (11.1.0) and confirmed by running a probe that references the package.

### Mapping rule

`KokoroVoiceManager.LoadVoicesFromPath` registers every `*.npy` under the folder with `Name = file name without extension` and `Features = float[510,1,256]`. There is no metadata; language comes from the name only (`KokoroLangCodeHelper.GetLanguage`):

1. Empty or whitespace name: American English.
2. `name.Length > 2` and `name[2] != '_'`: **American English** (this is how any non-conforming name is silently treated).
3. `name[0]` (case sensitive, so `A` fails) not one of `a b j z e f h i p`: American English.
4. Otherwise language = `name[0]`: `a` en-us, `b` en-gb, `j` ja, `z` cmn, `e` es, `f` fr, `h` hi, `i` it, `p` pt-br.

`name[1]` (`f` or `m`) is only a gender label for `GetVoices(language, gender)` filtering; synthesis never reads it. The G2P for en-us is the American Misaki pipeline, en-gb the British one, es/fr/hi/it/pt-br use eSpeak, ja and cmn their own G2Ps. `KokoroWavSynthesizer.SynthesizeAsync` calls `Tokenizer.Tokenize(text, voice.GetLangCode(), ...)` per call, so the choice is per voice, not per engine. `KokoroSynthesizer.ResolveVoice` in the plugin just calls `GetVoice(name)` (first case-insensitive name match wins) and falls back to `af_heart`.

### Probe output (Irish KVoiceWalk tensor `am_irishtest.npy`, 510x1x256)

| Registered name | Language | G2P | Tokens for the test sentence |
|---|---|---|---|
| `am_x`, `af_x`, `xm_x`, `AM_x`, `a` | American | en-us | 77 |
| `bm_x`, `bf_x`, `bm__isles_ie_m_01` | British | en-gb | 81 |
| `irish_x`, `isles.ie_m_01` (the manifest ids) | American (rule 2) | en-us | 77 |
| `zm_x` | Mandarin | cmn | n/a |

Test sentence: "The tomato is in the garage, and I can't dance in the water by the car park."
American: `ðə təmˈATO ɪz ɪn ðə ɡəɹˈɑʒ, ænd ˌI kˈænt dˈæns ɪn ðə wˈɔTəɹ bI ðə kˈɑɹ pˈɑɹk.`
British: `ðə təmˈɑːtQ ɪz ɪn ðə ɡˈaɹɑːʤ, and ˌI kˈɑːnt dˈɑːns ɪn ðə wˈɔːtə bI ðə kˈɑː pˈɑːk.`

SmokeSynth confirms they synthesize: `am_x` 4.97 to 4.99 s of audio, `bm_x` 5.08 s (British is about 90 ms longer: 4 more phonemes). Kokoro's output length varies by about 0.3 percent run to run (119301, 119787, 119455 samples for three identical runs), so do not compare lengths of single runs. The A/B WAVs are in `models/spike-work/s3out/` (`am_x.wav` American, `bm_x.wav` British, same tensor and text). **Which sounds better for an Irish tensor is a listening call I could not make**; the plan's human gate stays.

### Consequences for #9

* Do not put the public manifest id on the registry. Register with an internal name `{a|b}{f|m}__{slug}` (second character is a gender letter, third an underscore) so rule 2 holds. `bm__isles_ie_m_01` works exactly as the plan guessed.
* No need to copy `.npy` files to rename them: `KokoroVoiceManager.Voices` is a public `List<KokoroVoice>` and `KokoroVoice` can be built with `new KokoroVoice { Name = internalName, Features = array }` (or `KokoroVoice.FromPath` then `Rename`). Add directly after reading the pack. The registry is process-global and static, so unload on pack removal means removing from that list, and `GetVoice` returns the first match, so internal names must be unique and not collide with the stock 61.
* The manifest `phonemizer` field maps to the first letter: `a` or `b` (and later `e/f/h/i/p/j/z` if non-English packs ever appear). Keep the stock ids unchanged; `VoiceCatalog.Describe`'s prefix parsing can stay for legacy bare ids.
* Treat rule 2 as a trap in the loader: a pack voice registered under any name whose third character is not `_` silently becomes American, with no error. Add a unit test around the name builder.

## Recommendations

### #12 GPU runtime

1. Pin `Microsoft.ML.OnnxRuntime.EP.WebGpu` **0.2.1** for the LM path until upstream fixes 0.3.0/0.4.0 (file the repros). Re-run `tools/spikes/webgpu` against each new EP release; the `GroupQueryAttention` one-layer cut and `chain.onnx` are the two quick checks. Core ORT 1.24.4 or 1.31.0 both work with it.
2. The crash model is a process abort. Do not rely on a try/catch fallback and do not rely on an in-process self-test with a toy graph. Ship the `GpuCrashGuard` marker as designed, and gate GPU use on a pinned list of (EP version, model file hashes) that were verified. Everything outside the list runs CPU.
3. Backend selection: force `dawnBackendType=Vulkan` when Wine is detected. On native Windows D3D12 is still unproven (not testable here).
4. Wine only: copy `%windir%\system32\vulkan-1.dll` into the directory holding `onnxruntime_providers_webgpu.dll` before creating the first session (the plugin can do this at startup). Do not touch the game directory.
5. Adapter choice: expose Performance versus Low power (`powerPreference=high-performance` or `low-power`), not an adapter list that pretends to select a specific GPU. Enumerate adapters for display only, via `GetEpDevices()`, and know that without the launcher's DXVK `dxgi` the Wine list is a single fake "Radeon HD 5600".
6. Use the unprefixed option keys, pin them in a test, and treat unknown keys as silently ignored.
7. Kokoro does not get a GPU path. Remove "Kokoro GPU optional after S0" from the engine table. If the GPU toggle is global, it applies to Turbo only.
8. DirectML as a Wine fallback stays dead. Native-Windows DirectML fallback is unchanged.

### #13 Turbo

1. Turbo runs with the LM on the GPU and everything else on CPU: encoder (cached per reference clip), embedding, conditional decoder. Do not spend effort on the encoder or decoder on GPU; the decoder has STFT and random-noise nodes and aborts the EP.
2. Rewrite the decode loop around `OrtValue` and `OrtIoBinding` with device-resident KV exactly as in section S2. Managed KV is not an option on the GPU (31 to 37 ms per token and growing).
3. Use `Float16` KV tensors for fp16 and q4f16. q4f16 is the better default: 4.8 ms per token on the 9070 XT, 15.5 ms on the iGPU, 0.18 GB for the LM weights. fp16 only buys more numeric headroom; fp32 gives no speed advantage over fp16 on the GPU (13.6 vs 12.4).
4. Expected cost per 10 s of audio on this machine: LM about 1.2 s (q4f16) plus decoder about 3.3 s on CPU, RTF about 0.45 on the dGPU and about 0.72 on the iGPU. That passes the plan's "RTF below 0.8 for Cinematic" bar on the dGPU only. The decoder is the new bottleneck; chunked decoding (decode and play in segments while the LM continues) is the follow-up that helps most. Measure first, the CPU was shared with other jobs in my runs.
5. Keep the CPU path as the fallback and as the default for users without a verified GPU. The CPU fp32 numbers (118 ms per token) are why Turbo is unusable without the GPU.
6. Before shipping, A/B the GPU fp16 and q4f16 token streams against CPU fp32 with real text through the real tokenizer and listen; I only compared token ids.

### #9 Voice library

Use the S3 rule: internal registry name `{a|b}{gender}__{slug}`, derived from the manifest `phonemizer`; add voices through `KokoroVoiceManager.Voices`; unit-test the name builder; keep a listening gate for American versus British phonemization of each accent voice (the Irish A/B files are ready in `models/spike-work/s3out/`).

## Risks and open items

* EP 0.2.1 is the oldest release I tested (0.1.0 was not) and will not get fixes. Pinning it is a stopgap; the real exit is an upstream fix in a later EP release.
* Native Windows (D3D12) not covered by any of these runs.
* The speech encoder on EP 0.2.1 returns wrong values; the plan must keep it on CPU (it does).
* `forceCpuNodeNames` accepts only 8192 characters, so bisecting large graphs needs several runs.
* Not tested: in-process inside Dalamud (S4), VRAM use, frame time impact on the game, a second Vulkan instance next to DXVK in the same process, and a cold shader cache.
* Timings are from a busy machine; treat ratios, not absolute values.
