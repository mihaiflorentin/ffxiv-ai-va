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
tools/SmokeSynth/             Plain console harness for the synthesis engine (no Dalamud); also the voice pack validator/auditioner
tools/voicepack/              Voice pack generator (dataset -> KVoiceWalk -> style tensor, clip, preview, manifest); see its README
src/AIVoiceActing/voicepacks/ Bundled voice packs (aiva-isles); copied next to the plugin dll by the csproj
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
# Turbo device plan: --engine turbo --device cpu|gpu [--api vulkan|d3d12] [--power high|low] [--bench]
# prints the probe (platform, adapters), the plan and the engine status. The GPU runtime is the
# WebGPU EP pinned to exactly 0.2.1 (0.3.0/0.4.0 abort the process on the Turbo graphs); GPU
# integration tests run only with AIVA_GPU_TESTS=1 (they need a real GPU, so not in the sandbox).
```

Voice packs (`voicepack.json`, schema 1; see `Domain/VoicePackManifest`) are authored and checked with
`tools/voicepack` and SmokeSynth. Everything heavy (datasets, KVoiceWalk venv, walk output) lives in
`models/kvoicewalk-work` (gitignored); `tools/voicepack/README.md` has the setup and the one-command
regeneration of a single voice (`python3 tools/voicepack/build_voice.py isles.sc_m_01`):

```bash
# Check a pack with the plugin's own manifest parser + library scan (exit 1 when invalid):
dotnet run --project tools/SmokeSynth -c Release -p:UseAppHost=false -p:AllowMissingPrunePackageData=true -- \
  --validate-pack src/AIVoiceActing/voicepacks/aiva-isles
# Render every voice's preview line with Kokoro (--only <voice id> for one; --text to change the line):
dotnet run --project tools/SmokeSynth -c Release -p:UseAppHost=false -p:AllowMissingPrunePackageData=true -- \
  --audition src/AIVoiceActing/voicepacks/aiva-isles --out /some/dir --models "$HOME/.cache/aiva-kokoro"
```

Pack data is CC-BY-SA-4.0 derived from `ylacombe/english_dialects`: keep the pack's LICENSE and NOTICE,
and mark new voices `experimental` until someone has listened to them. Casting sets for pack voices live in
`Domain/voices.json` (`RaceVoiceMap` drops slots whose pack is missing or disabled).

The tests project and the SmokeSynth tool must NOT reference Dalamud; the plugin project's
csproj carries the runtime packages (OnnxRuntime, DirectML, NAudio, Tokenizers.DotNet,
Standart.Hash.xxHash, KokoroSharp, the WebGPU EP pinned to 0.2.1). NuGet packages are added in the csproj that owns
their runtime use, never transitively "while convenient".

# Dev install (no GitHub releases): unzip latest.zip into ~/.xlcore/devPlugins/AIVoiceActing/ ONLY
# ("Scan Dev Plugins" in the installer). Never also unpack it into installedPlugins: Dalamud then loads two
# copies (duplicate commands, two GPU runtimes in one process).

## Architecture in brief

- **Quality tiers** (`Domain/SetupAdvisor`, the single source): Fast = Kokoro, Balanced = Kokoro plus Turbo for strongly emotional lines, Most expressive = Turbo for every line (needs a working GPU). Players choose a tier on Home ("Set up recommended") or Settings; raw engine names live under Settings > Advanced only. `UI/State/SetupFlowModel` and `QualityTierModel` are thin views over the advisor.
- **GPU runtime**: Turbo runs on the ONNX Runtime WebGPU EP (pinned to exactly 0.2.1; Vulkan on Linux/Wine, D3D12 on Windows) via `Infrastructure/Onnx` (`DevicePlanner`, `DeviceProbe`, `WebGpuRuntime`, `VerifiedGpuCombos`). `GpuCrashGuard` marks a crash in flight and keeps the GPU off until the player retries; the CPU is always the fallback.
- **Voices**: `IVoiceLibrary` merges the stock Kokoro voices with voice packs (`voicepack.json`, `Domain/VoicePackManifest`). Bundled packs live in `src/AIVoiceActing/voicepacks/` (`aiva-isles`, `aiva-accented`); players can add folders. `Domain/voices.json` is the built-in casting. Its rules (no American voices, English only, sex-matched, lore-based) are in `docs/casting-lore.md` and enforced by `CastingLoreTests`. A voice that cannot load falls back to a British stand-in of the same sex (`VoiceCatalog.FallbackFor`: `bf_emma` / `bm_george`), never an American one. Old `af_*`/`am_*` ids are still recognised so saved configs keep working.
- **Voice pack tooling**: `tools/voicepack` generates packs (see its README); `tools/SmokeSynth --validate-pack` and `--audition` check and render them.
- **UI** (`src/AIVoiceActing/UI`): four tabs (Home, Voices, What's read, Settings) plus the Now Speaking overlay (`NowSpeakingWindow`: voice fingerprint, friendly voice name, mood, Replay/Skip/Stop, gear to the Voices tab via `NavModel.OpenAt`). Rules enforced by `UiHygieneTests` over the whole UI tree with no allowlist: sizes in em (`Ui.Em`, `Ui.Scaled`), no pixel literals, no emoji (FontAwesome via `IconButton`, which requires a tooltip). `Config.ReduceMotion` turns off fingerprint pulses and indeterminate progress animation. Modals go through `ModalHost` (Esc closes). Pure view logic lives in `UI/State` and is unit tested. The manual checks are in `docs/plans/qa-matrix.md`.

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
