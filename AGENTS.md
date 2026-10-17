# AGENTS.md — AIVoiceActing

AI voice acting for FFXIV: local neural TTS (Chatterbox ONNX) with emotion-aware delivery, persistent per-character voices, and courtesy for the game's own voiced lines. Hexagonal (ports & adapters) architecture, mirroring the conventions of `~/Workspace/ffxiv-census`.

## Layout

```
AIVoiceActing.sln
src/AIVoiceActing/            Dalamud plugin (Dalamud.NET.Sdk/15.0.0, net10.0-windows)
  AIVoiceActingPlugin.cs      Entry: [PluginService] statics, builds the container, disposes it
  AIVoiceActing.json          Plugin manifest (Name/Author/Punchline/Description required by DalamudPackager)
  Domain/                     Pure logic (records, rules). References Ports types only
  Domain/Handlers/            Driving handlers (pipeline) — later steps
  Ports/                      Contracts: exceptions first, DTO records next, interface last, one file per port
  Infrastructure/             Driven adapters implementing ports (Dalamud, Onnx, Storage, Net, Audio, Text)
  Container/                  ServiceContainer: sole composition root, lazy lock-guarded port accessors
  UI/                         ImGui windows (driving adapter) — later steps; calls ports only
tests/AIVoiceActing.Tests/    xunit; fakes in Mock/ (census fake pattern)
tools/SmokeSynth/             Plain console harness for the synthesis engine (no Dalamud)
```

## Dependency rules (enforced by `ArchitectureScanTests`)

- `AIVoiceActing.Domain.*` and `AIVoiceActing.Ports.*` must reference NOTHING in
  `AIVoiceActing.Infrastructure.*`, `AIVoiceActing.UI.*`, or any `Dalamud*` / `FFXIVClientStructs*` / `Lumina*` namespace.
- Infrastructure implements ports; UI consumes ports only; the Container composes adapters and returns port-typed handles.
- The container (`Container/ServiceContainer`) is deliberately Dalamud-free so unit tests can
  exercise it on any OS; the plugin entry wires Dalamud adapters into it.
- Scan limitation (documented in the test): declared metadata only (base types, interfaces,
  fields, properties, events, method signatures); method bodies are not decompiled.
- Note: the plugin assembly and Dalamud.dll are x64 PE images, and a `net10.0-windows` test
  host requires the WindowsDesktop shared framework — neither loads on an arm64 macOS host.
  Tests therefore compile the Dalamud-free sources (Domain/Ports/Container) directly; see the
  comment in `tests/AIVoiceActing.Tests/AIVoiceActing.Tests.csproj`.

## Build & verify

Every command that builds the plugin or the tests needs the Dalamud assemblies:

```bash
export DALAMUD_HOME="$HOME/Workspace/ffxiv-ai-va/.dalamud/dev"

# Distro SDK (10.0.12) lacks PrunePackageData, hence AllowMissingPrunePackageData.
dotnet build src/AIVoiceActing/AIVoiceActing.csproj -c Release -p:AllowMissingPrunePackageData=true
dotnet test tests/AIVoiceActing.Tests/AIVoiceActing.Tests.csproj -c Release -p:NetCoreTargetingPackRoot=$HOME/.cache/dotnet-official-packs/packs
dotnet publish src/AIVoiceActing/AIVoiceActing.csproj -c Release -p:AllowMissingPrunePackageData=true   # produces latest.zip (DalamudPackager)

# Engine smoke harness (no Dalamud needed). --engine kokoro is the default (needs --models
# pointing at a dir with kokoro-v1.0.onnx and --ref <voice name> such as af_heart).
# --engine turbo [--device cpu|gpu] [--precision auto|fp32|fp16|q4f16] [--bench] runs Chatterbox
# Turbo from a models dir with the catalog's local file names; the GPU needs a Vulkan device
# (it is hidden inside the Claude sandbox):
dotnet run --project tools/SmokeSynth -c Release -p:UseAppHost=false -p:AllowMissingPrunePackageData=true -- \
  --engine kokoro --text "Life is but a dream." --ref af_heart \
  --models "$HOME/.cache/aiva-kokoro" --out /tmp/aiva-test.wav --rtf
# Turbo on the GPU (outside the sandbox: it hides /dev/dri). --stream uses the chunked decode and
# reports time to first audio; --bench repeats it five times (see docs/plans/2026-10-gpu-spike-results.md).
dotnet run --project tools/SmokeSynth -c Release -p:UseAppHost=false -p:AllowMissingPrunePackageData=true -- \
  --engine turbo --device gpu --precision q4f16 --stream --bench --models <dir with the turbo files> \
  --text "..." --out /tmp/aiva-turbo.wav
```

# Turbo device plan: --engine turbo --device cpu|gpu [--api vulkan|d3d12] [--power high|low] [--bench]
# prints the probe (platform, adapters), the plan and the engine status. The GPU runtime is the
# WebGPU EP pinned to exactly 0.2.1 (0.3.0/0.4.0 abort the process on the Turbo graphs); GPU
# integration tests run only with AIVA_GPU_TESTS=1 (they need a real GPU, so not in the sandbox).

The tests project and the SmokeSynth tool must NOT reference Dalamud; the plugin project's
csproj carries the runtime packages (OnnxRuntime, DirectML, NAudio, Tokenizers.DotNet,
Standart.Hash.xxHash, KokoroSharp, the WebGPU EP pinned to 0.2.1). NuGet packages are added in the csproj that owns
their runtime use, never transitively "while convenient".

# Dev install (no GitHub releases): unzip latest.zip into
# ~/.xlcore/installedPlugins/AIVoiceActing/<version>/ (boot scan) and
# ~/.xlcore/devPlugins/AIVoiceActing/ ("Scan Dev Plugins" in the installer).

## Conventions

- Port file anatomy (census style): sentinel exceptions → DTO records → interface last; one port per file.
- Fakes live in `tests/AIVoiceActing.Tests/Mock/`, record their calls, and assert conformance via assignment (`ILogSink _ = new FakeLogSink();` style intent).
- Turbo downloads are the base group (`turbo-base`: tokenizer, default voice) plus ONE precision set (`turbo` fp32 for the CPU, `turbo-fp16`, or `turbo-q4f16`, the 0.56 GB graphics card install); `Domain/TurboInstall` is the single rule for what counts as installed, and every catalog group has exactly one owning engine in `EngineCatalog`.
- Plugin manifest fields `Name`, `Author`, `Description`, `Punchline` are mandatory (DalamudPackager hard-fails otherwise).
- `obj/`, `bin/`, `.dalamud/`, `.omp/`, `.superpowers/`, `ConfigDirectory/`, `models/` are never committed.

## Agent skills

### Issue tracker

Issues and specs live in GitHub Issues for `mihaiflorentin/ffxiv-ai-va` (via `gh`). See `docs/agents/issue-tracker.md`.

### Triage labels

Default vocabulary: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` + `docs/adr/` at the repo root (created lazily). See `docs/agents/domain.md`.
