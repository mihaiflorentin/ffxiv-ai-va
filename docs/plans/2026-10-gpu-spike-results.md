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
| Turbo decoder on the CPU is the limit | Fixed by **chunked decoding** (issue #23, last section): first audio after about 1.4 s median instead of the whole line (10.8 s), measured on a 4x oversubscribed machine; the GPU install shrinks from 3.62 GiB to 540 MiB. |

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

## Chunked Turbo decode and the small GPU install (issue #23)

Date: 2026-10-17. Follow-up to #13. Code: `Domain/ChunkSchedule`, `Domain/ChunkStitcher`, `Domain/SpeechAudioStream`, `Domain/PolishedPcmSource`, `Domain/TurboInstall`, `Infrastructure/Onnx/ChatterboxSynthesizer` (`SynthesizeStreamingAsync`), `SpeechTokenFeed`, `PromptTrim`, `StreamingOptions`. Harness: `tools/SmokeSynth` (`--stream`, `--bench`, `--lines`, and the chunk knobs). A/B audio: `models/turbo-ab/chunked-*.wav` (gitignored).

**Measurement conditions, read this first.** The machine was saturated by other agents for the whole session: load average 57 to 65 on 16 logical CPUs, all 16 cores at 100 percent, 55 GB of swap in use. Every wall-clock number below is therefore 3 to 4 times worse than an idle machine would give, and noisy (run-to-run spread of 2x). Where a number is robust against load I use CPU seconds of a single-thread run (`process_time`), and I say so. Numbers marked "projection" are arithmetic on those CPU seconds, not measurements. Re-run `SmokeSynth --stream --bench` on a quiet machine before quoting the absolute values.

### What the decoder graph offers

`conditional_decoder` (fp32, fp16 and q4f16 alike) takes `speech_tokens [1, n]`, `speaker_embeddings [1, 192]` and `speaker_features [1, frames, 80]` and returns `waveform [1, samples]`. There are **no state or context inputs**, so it cannot be told what came before. What it does have is the reference prompt: the first `frames / 2` tokens of `speech_tokens` are the prompt tokens of the reference clip, `speaker_features` is the mel of the same audio, and the output holds audio only for the tokens after the prompt, exactly 960 samples per token (25 tokens per second at 24 kHz; checked on 214 tokens, 205 440 samples). So the prompt is the decoder's only "context mechanism", and it is fixed to the reference clip: continuing from the audio just generated would need a mel extractor for it (not built).

The decoder attends over all its tokens in both directions, so audio near either edge of a window is worse than in the middle. The chunker therefore decodes each chunk with 12 tokens of already-known context before it and up to 6 tokens of lookahead after it, keeps only the middle, and cross-fades the overlap into the neighbouring chunk's audio with an equal-power sine/cosine fade of 80 ms (960 samples either side of the boundary). Chunks are 32, 48, 72, 108, 120, 120 ... tokens (1.3 s, 1.9 s, 2.9 s, 4.3 s, 4.8 s ...); a tail shorter than 16 tokens joins the last chunk. A chunk is released once its 6 lookahead tokens exist, so on a GPU language model (about 100 tokens per second) the first window is ready after about 38 tokens.

### Decoder cost on the CPU

CPU seconds, one thread, one decoder call, prompt tokens kept (rows) against new tokens decoded (columns). Machine load made wall time useless, CPU time is stable to about 15 percent.

| Variant | prompt 186: 30 / 60 / 120 new | prompt 100: 30 / 60 / 120 | prompt 50: 30 / 60 / 120 | prompt 25: 30 / 60 / 120 |
|---|---|---|---|---|
| fp32 | 1.37 / 2.00 / 3.30 | 0.99 / 1.57 / 1.99 | 0.52 / 1.01 / 1.71 | 0.46 / 0.79 / 1.68 |
| fp16 | 1.56 / 1.94 / 2.82 | 0.99 / 1.35 / 2.32 | 0.74 / 0.94 / 1.86 | 0.69 / 1.14 / 1.74 |
| q4f16 | 1.53 / 2.06 / 3.49 | 0.95 / 1.25 / 2.27 | 0.58 / 0.94 / 2.17 | 0.56 / 0.99 / 1.75 |

* **The prompt is most of the cost of a small chunk.** The full 186-token prompt makes a 30-token call cost 1.4 to 1.5 s; trimming it to 100 cuts that by about a third, to 50 by about two thirds. The cost is roughly linear in prompt plus new tokens.
* **fp16 and q4f16 are not faster than fp32 on the CPU** (no half-precision or 4-bit hardware path in the CPU kernels): the same call costs 0.71 s (fp32), 0.73 s (fp16), 0.76 s (q4f16) on one thread for a 60-token prompt plus 40 new tokens. The precision choice for the CPU graphs is about disk size and accuracy, not speed.
* **Threads.** Same call, CPU seconds / wall seconds (wall is inflated by the load): fp32 1 thread 0.71 / 1.17, 2 threads 1.17 / 0.97, 4 threads 1.89 / 1.04, 8 threads 3.45 / 1.50; fp16 and q4f16 follow the same pattern (q4f16: 0.76, 1.43, 2.69, 5.54 CPU seconds). More threads burn CPU spinning for a small gain, and a streamed line is a series of short calls. A one-thread chunk already decodes faster than real time (0.98 CPU seconds for the first 1.5 s of audio), so the **decoder now defaults to 2 intra-op threads** (`OnnxSessionFactory.DecoderDefaultMaxThreads`); an explicit CPU impact setting is still respected. The language model session and the other CPU sessions keep the old default.
* **Session options do not matter.** fp32, 4 threads, CPU seconds / wall: graph optimization disabled 2.54 / 1.57, basic 2.61 / 1.29, extended 2.48 / 2.04, all (default) 2.43 / 1.55; memory pattern off 2.52 / 2.07; parallel execution mode 2.55 / 1.56. All inside the noise, so the defaults stay.

### Which precision for the CPU graphs

Ten FFXIV-style lines (`models/turbo-ab/chunked-lines.txt`), tokens from the real q4f16 LM on the GPU, decoded on the CPU with each combination of encoder (reference prompt) and decoder precision, transcribed with faster-whisper `small.en`, word error rate against the text, speaker likeness as the cosine between the speech-encoder x-vector of the output and of the reference clip.

| Encoder / decoder | Mean word error rate | Speaker cosine |
|---|---|---|
| q4f16 / q4f16 (what ships) | 0.028 | 0.9141 |
| fp32 / fp32 | 0.028 | 0.9169 |
| fp32 prompt / q4f16 decoder | 0.028 | 0.9151 |
| fp16 / fp16 | 0.028 | 0.9099 |
| q4f16 prompt / fp32 decoder | 0.044 (one line, one word) | 0.9187 |

The per-line word error rates of the first four rows are identical, and the spread in speaker cosine (0.910 to 0.919) is the noise of the metric. The q4f16 encoder does change the prompt (only 42 percent of its prompt tokens equal the fp32 encoder's, x-vector cosine 1.000, prompt mel cosine 0.9992), and the decoder is deterministic for equal inputs, but the speech does not get less intelligible or less like the voice. **Decision: the CPU graphs (encoder, embedding, decoder) are the q4f16 ones, 374 MB instead of 2.0 GB.** They already were what the GPU path loaded; what changes is that the fp32 files are no longer required next to them.

### Prompt length in the chunked path

Same ten lines, q4f16 everywhere, the prompt tail kept for the decoder:

| Prompt tokens kept | 186 (all) | 120 | 100 | 80 | 60 | 40 |
|---|---|---|---|---|---|---|
| Mean word error rate | 0.028 | 0.028 | 0.028 | 0.028 | 0.034 | 0.021 |
| Speaker cosine | 0.9141 | 0.9126 | 0.9120 | 0.8998 | 0.9048 | 0.8865 |

Likeness holds down to 100 tokens and falls from there; with 40 the log-mel distance to the full-prompt decode doubles (1.26 against 0.93 at 60) and the level drops by up to 43 percent on one line. **The streaming path keeps the last 100 prompt tokens** (`StreamingOptions.PromptTokens`); the whole-line path is unchanged and keeps all 186.

### Do chunks match the whole decode?

Final defaults (first chunk 32, growth 1.5, cap 120, context 12 and 6, 80 ms fade, 100 prompt tokens), the same ten lines, streamed by `SmokeSynth --stream --lines` against the whole-line decode:

* The speech tokens are identical for all ten lines (chunking only changes the decoder, not the greedy loop), and the audio length is identical to the sample.
* Word error rate: whole 0.034, streamed 0.029 (line 6 lost a word, line 2 gained back more).
* Sample-level edge near the seams (largest second difference within 20 ms of a seam over the line's 99.9th percentile): worst 0.63 streamed, 0.81 for the undisturbed whole decode, so the seams are not sharper than the speech. The energy at a seam relative to its surroundings equals the whole decode's wherever there is speech (the 0.01 worst case is a pause that both versions have).
* Waveforms differ between the two decodes (the vocoder's harmonic phases are random per call), which is why the fade is equal-power; the log-mel distance between the two versions is 0.91 on average, against 0.82 for the same comparison with the full prompt (a Python re-implementation of the chunker with a 40 ms fade), i.e. much of it is the prompt trim, not the seams.
* Other fade/context choices tried on the same lines: 40 ms with 8 and 4 tokens, 20 ms with 8 and 4. All keep the word error rate; one 40 ms seam had a sharper edge (1.41 against 0.77 for the control), the 80 ms setting did not. Not a listening test: the A/B files need a human ear (`models/turbo-ab/chunked-whole.wav` against `chunked-stream.wav`, and `chunked-whole-lineN.wav` against `chunked-stream-lineN.wav` for the ten lines).

### Time to first audio

Line: "The Echo stirs again, doesn't it? I have seen that look before. Come, let us find shelter before the storm." 189 speech tokens, 7.68 s of audio, q4f16 on the GPU (WebGPU, Vulkan, performance preference), five runs after the warm-up run, load average 57 to 62 on 16 CPUs.

| Path | First audio min / median / max | Time to end of line, RTF min / median / max |
|---|---|---|
| Whole line, decoder on 8 threads (as shipped in #13) | 7.6 / 10.8 / 13.1 s (the end of the line) | 0.99 / 1.41 / 1.70 |
| Whole line, decoder on 2 threads | 9.4 / 10.1 / 12.9 s | 1.23 / 1.32 / 1.67 |
| **Chunked, decoder on 2 threads, final defaults** | **1.2 / 1.4 / 3.5 s** | 0.93 / 1.42 / 2.09 |

Two earlier chunked runs of nearly the same settings gave first-audio medians of 1.55 s and 1.72 s (max 2.6 s and 2.5 s). The same ten lines streamed one by one gave a median of 2.2 s (1.6 to 4.0 s, the first includes the warm-up) against 4.4 to 7.6 s before any sound for the whole-line decode of those lines. The very first line after a start is slower in both modes (13 to 22 s here: session creation, the GPU shader compile and encoding the reference clip); that is what `WarmUpAsync` is for.

Total time barely moves: the language model took 1.4 to 2.8 s for the 189 tokens in every variant and the decoder 7 to 16 s in all of them. Chunking is not free, though: the four windows of this line cost **5.0 CPU seconds** on one thread against **3.9** for the single whole-line call (+28 percent, the prompt is read four times), 0.98 of them for the first window.

**Projection for an idle machine (not measured):** first window 0.98 CPU seconds, about 0.6 s of wall time on two threads at a 1.6x speed-up, plus about 0.3 s for the language model to produce 38 tokens at 5 to 10 ms each, so about 1 s to first audio, comfortably under the 2 s target; the whole line at roughly the language model time plus the last window, i.e. an RTF around 0.3 to 0.4. Under the 4x oversubscription measured here the 2 s target was met by the median and missed by single runs (max 3.5 s).

### Install sizes

Before this change the engine required the whole `turbo` group (fp32 graphs, 3.1 GiB) next to the q4f16 build, so a graphics card needed 3.62 GiB. Now a Turbo install is the base group plus ONE precision set (`Domain/TurboInstall`):

| Group | Contents | Size |
|---|---|---|
| `turbo-base` (new) | tokenizer, default reference voice | 4 276 592 B (4.1 MiB) |
| `turbo` | fp32 encoder, embedding, language model, decoder | 3 319 114 844 B (3.09 GiB) |
| `turbo-fp16` | the four fp16 graphs | 1 661 100 256 B (1.55 GiB) |
| `turbo-q4f16` | the four q4f16 graphs: encoder 178.5 MB, embedding 33.7 MB, language model 184.3 MB, decoder 165.4 MB | 561 806 478 B (535.8 MiB) |

* **Graphics card install: `turbo-base` + `turbo-q4f16` = 566 083 070 B (539.9 MiB), 85 percent smaller than the old 3 885 197 914 B (3.62 GiB).**
* CPU install: `turbo-base` + `turbo` = 3 323 391 436 B (3.10 GiB), same files as before (the same local names; two catalog entries just moved to `turbo-base`, so existing installs stay "downloaded").
* `SetupAdvisor.GroupsFor`: Balanced is Kokoro + `turbo-base` + (`turbo-q4f16` with a GPU, `turbo` without); Most expressive is `turbo-base` + the same set. The Home card and the Settings quality card plan from per-group statuses (`EngineParts.TurboGroupStatuses`); what the UI treats as required for Turbo is the base group plus the set the machine already has, else the one it should download (`TurboInstall.RequiredGroups`).
* Readiness: the engine is ready with the base files and any complete set. With only q4f16 present and the GPU switched off (crash guard) it speaks from q4f16 on the CPU instead of demanding the fp32 files; `ExpressiveEngineInstalled` uses the same rule.
* `EngineCatalog`: the Turbo engine owns `turbo-base`, `turbo`, `turbo-fp16`, `turbo-q4f16` (every catalog group still has exactly one owner, enforced by `TurboInstallTests`).

### Streaming path in the plugin

`ISpeechSynthesizer` gained `SupportsStreaming`, `SupportsStreamingFor(request)`, `StreamingSampleRate` and `SynthesizeStreamingAsync(request, onSamples, ct)`; Turbo streams when its language model runs on the GPU (the CPU model is slower than real time, so the whole-line path wastes less there). The handler queues a `SpeechItem` carrying a `SpeechAudioStream` as soon as the first piece exists, so lines keep the order they started rendering in, and caches the joined line afterwards (the render cache is filled with what was streamed; the whole-line path and its cache key are untouched, and `RenderVariant` keeps GPU and CPU renders apart). `PlaybackSpeechQueue` plays the stream through `IAudioSink.PlayStream` (`NAudioSink` feeds the device from a live source and fills a synthesizer stall with silence; a sink that cannot stream gets the whole line once it is complete), applying gain, soft limiter and the 6 ms edge fades on the way out; loudness levelling measures the first half second. Skipping the line cancels the stream, which cancels the synthesizer (language model loop and decoder), and the rest of that line's sentences are dropped as before. The expressive router streams its expressive engine's lines too (a calm Kokoro line still renders whole). The toggle is the existing "Start long lines sooner" setting.

### Risks and follow-ups

* Every absolute timing here comes from an oversubscribed machine; the idle numbers are projections.
* The seams passed objective checks (identical tokens, word error rate, edge and level metrics); nobody has listened to them. Listen to the A/B files before shipping.
* `NAudioSink.PlayStream` compiles and is covered through the fake sink and the queue tests, but was not run on Windows or under Wine. Check in game: start of the stream, an underrun on a loaded machine, Skip during a stream.
* Chunking costs about 28 percent more decoder CPU per line. On a machine that cannot keep up it turns "slow but silent until the end" into "audible early with gaps"; turning "Start long lines sooner" off restores the old behaviour.
* A mid-stream GPU failure after audio has started ends the line (nothing can re-voice it); before the first audio it restarts the line on the CPU as before.
* The gated test `Turbo_Q4f16_OnTheGpu_StartsLikeTheCpuStream_AndEndsAboutAsLong` fails on this machine (GPU and CPU greedy streams diverge at token 53 of 135 and 180). It exercises only the whole-line path, which this change does not touch, but I did not run it on the base commit to prove it fails there too.
* Not done: continuing from the previously generated audio (needs a mel front end for the decoder prompt), an adaptive first-chunk size from measured decoder speed, a Windows/Wine measurement.
