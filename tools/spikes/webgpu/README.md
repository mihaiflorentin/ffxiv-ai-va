# WebGPU / Turbo spike harness

Scratch console app behind `docs/plans/2026-10-gpu-spike-results.md` (issue #8). Not in the solution, no plugin code.

```bash
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
# Linux native. Of the EP releases tested, 0.2.1 is the only one where the Turbo LM runs; 0.4.0 is the latest.
dotnet build tools/spikes/webgpu/Spike.csproj -c Release -p:EpVer=0.2.1 -p:OrtVer=1.24.4
dotnet tools/spikes/webgpu/bin/Release/net10.0/Spike.dll enum

# win-x64 self-contained for the XIVLauncher Wine
dotnet publish tools/spikes/webgpu/Spike.csproj -c Release -p:RuntimeIdentifier=win-x64 \
  -p:SelfContained=true -p:UseAppHost=true -o <out>
```

Modes (`Spike <mode> key=value ...`):

| Mode | Purpose | Main keys |
|---|---|---|
| `enum` | Register the EP library and list EP devices | `eplib=<abs path>` |
| `inspect` | Print graph inputs/outputs | `model=` |
| `load` | Create a session on cpu or webgpu | `model= device= dev= opts=k:v,k:v log=verbose forcecpu=<names or file>` |
| `runmodel` | One run with synthetic inputs (repro graphs, cut LM graphs) | same as `load` |
| `embed` | Embedding graph smoke run | same as `load` |
| `kokoro` | Kokoro run with token ids, prints first/steady RTF | `model= voice=<510x1x256 .npy> ntok= runs= device= outwav=` |
| `turbo` | Encoder, embed, LM decode loop, decoder with per-phase timings | `dir= prec=fp32/fp16/q4f16 all= enc= emb= lm= dec= kv=managed/iobind steps= ref=` |
| `loadlib` | `LoadLibraryExA` probe (Wine) | `name=` |

`opts` goes verbatim to `SessionOptions.AppendExecutionProvider(env, devices, options)`. Keys are unprefixed (`dawnBackendType`, not `ep.webgpuexecutionprovider.dawnBackendType`).

`repro/genrepro.py` builds the minimal graphs that abort the EP. `repro/DmlProbe.cs.txt` is the DirectML-under-Wine probe (needs its own csproj with `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4, win-x64).
