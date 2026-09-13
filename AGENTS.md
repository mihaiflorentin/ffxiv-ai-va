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

# Engine smoke harness (no Dalamud needed). --engine kokoro is the default in-game
# engine (needs --models pointing at a dir with kokoro-v1.0.onnx and --ref <voice name>
# such as af_heart); omit --engine for the legacy Chatterbox path:
dotnet run --project tools/SmokeSynth -c Release -p:UseAppHost=false -p:AllowMissingPrunePackageData=true -- \
  --engine kokoro --text "Life is but a dream." --ref af_heart \
  --models "$HOME/.cache/aiva-kokoro" --out /tmp/aiva-test.wav --rtf
```

The tests project and the SmokeSynth tool must NOT reference Dalamud; the plugin project's
csproj carries the runtime packages (OnnxRuntime, DirectML, NAudio, Tokenizers.DotNet,
Standart.Hash.xxHash, KokoroSharp). NuGet packages are added in the csproj that owns
their runtime use, never transitively "while convenient".

# Dev install (no GitHub releases): unzip latest.zip into
# ~/.xlcore/installedPlugins/AIVoiceActing/<version>/ (boot scan) and
# ~/.xlcore/devPlugins/AIVoiceActing/ ("Scan Dev Plugins" in the installer).

## Conventions

- Port file anatomy (census style): sentinel exceptions → DTO records → interface last; one port per file.
- Fakes live in `tests/AIVoiceActing.Tests/Mock/`, record their calls, and assert conformance via assignment (`ILogSink _ = new FakeLogSink();` style intent).
- Plugin manifest fields `Name`, `Author`, `Description`, `Punchline` are mandatory (DalamudPackager hard-fails otherwise).
- `obj/`, `bin/`, `.dalamud/`, `.omp/`, `.superpowers/`, `ConfigDirectory/`, `models/` are never committed.
