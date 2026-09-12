# AIVoiceActing

Local AI voice acting for FFXIV. The plugin captures quest Talk dialogue, BattleTalk, cutscene lines, and chat channels, and speaks each line aloud with a locally-running neural TTS model (Chatterbox, ONNX). No cloud services, no Python sidecar — inference runs in-process through ONNX Runtime.

- Every character gets a **persistent voice**: assigned deterministically from race/tribe/gender (`xxHash32` of the character key), stored in `voice-assignments.json`, and never reassigned. Manual per-NPC and per-player overrides win.
- **Delivery is shaped by context**: a rules-based emotion director (zero cost, always on) sets exaggeration, pacing, and paralinguistic style per line; an optional small LLM director asset can read the recent cutscene dialogue for finer direction.
- **The game's own voice acting always wins**: when a voiced line plays in-game, the plugin cancels its speech and stays silent for that line.

English only in v1.

## Requirements

- Windows 10/11 for in-game use (FFXIV + Dalamud via XIVLauncher). The audio sink (WASAPI) and the DirectML execution provider are Windows-only.
- .NET 10 desktop runtime, installed by XIVLauncher/Dalamud.
- ~1.1 GB of model files, downloaded once from the plugin's **Models** tab on first use (download only starts when you press the button).
- macOS: the engine runs for local development (CPU; CoreML selectable), and XIV on Mac builds load with the CPU execution provider — see [GPU notes](#gpu-notes).

## Models and licenses

| Asset | Source | License |
|---|---|---|
| Chatterbox 0.5B voice model (speech encoder, embed tokens, q4 language model, conditional decoder) | [`onnx-community/chatterbox-ONNX`](https://huggingface.co/onnx-community/chatterbox-ONNX) (ONNX export of [Resemble AI's Chatterbox](https://github.com/resemble-ai/chatterbox)) | MIT (model and export) |
| `default_voice.wav` reference clip | [`onnx-community/chatterbox-ONNX`](https://huggingface.co/onnx-community/chatterbox-ONNX) | MIT |
| Tokenizer (`tokenizer.json`) | [`onnx-community/chatterbox-ONNX`](https://huggingface.co/onnx-community/chatterbox-ONNX) | MIT |
| Optional emotion-director LLM (Qwen3-0.6B ONNX; not required, off by default) | Qwen3 | Apache-2.0 |

Models are downloaded to `ConfigDirectory/models/` at runtime; nothing model-sized ships in the plugin zip.

## Install

Plugin repository (after first tagged release):

```
https://<owner>.github.io/<repo>/repo.json
```

Manual install: download `latest.zip` from a GitHub release and unzip it into `%AppData%\XIVLauncher\installedPlugins\AIVoiceActing\` (or install via any Dalamud plugin repo pointing at the zip). Launch the game, open `/aivaconfig`, and use the **Models** tab to download the model files.

## Building from source

Requires the .NET 10 SDK and a Dalamud development checkout for assembly references.

```sh
git clone <this repository>
cd ffxiv-ai-va

# Dalamud reference assemblies (also used by CI):
curl -fsSL https://goatcorp.github.io/dalamud-distrib/latest.zip -o dalamud.zip
unzip -q dalamud.zip -d .dalamud/dev

export DALAMUD_HOME="$PWD/.dalamud/dev"

dotnet build AIVoiceActing.sln -c Release
dotnet test                       # ~380 unit tests
dotnet publish src/AIVoiceActing -c Release
```

`dotnet publish` produces `src/AIVoiceActing/bin/Release/AIVoiceActing/latest.zip` — the shippable plugin archive (plugin dll + deps + manifest + icon + `voices/` + required natives).

CI (`.github/workflows/release.yml`) mirrors this: it builds on every push/PR, runs the test suite, and on a `v*` tag publishes `latest.zip` to a GitHub release and `repo.json` to the `gh-pages` branch (set GitHub Pages to "Deploy from branch: gh-pages" once).

## GPU notes

Synthesis runs through ONNX Runtime execution providers, on a background thread — the game thread is never blocked.

- **Windows** (default): **DirectML** — works on AMD and NVIDIA GPUs. CPU is the automatic fallback. Select the provider in **Speech Settings → Engine**.
- **macOS** (XIV on Mac): **CPU** is the default and the usable choice today. CoreML is selectable, but the quantized language-model graph partitions into ~3,800 segments under CoreML and is not practically usable.
- **Honest performance numbers**: CPU real-time factor is roughly 8 (one second of audio takes about eight seconds) on an Apple M1; expect similar or better on a mid-range Windows laptop, and substantially faster under DirectML on a discrete GPU. Turn-based dialogue tolerates this; fast chat backlogs will lag behind on CPU.

## Voiced-line courtesy

The game ships real voice acting for main-story cutscenes and some quest dialogue. The plugin hooks the game's sound-playback path and detects voiced `.scd` lines:

- When the game plays a real voice line, any speech the plugin is producing is cancelled immediately.
- `SkipVoicedQuestText` and `SkipVoicedBattleText` (both **on** by default) stop the plugin from synthesizing those lines at all.

`CancelSpeechOnTextAdvance` (on by default) also stops speech when you advance dialogue.

## Slash commands

| Command | Effect |
|---|---|
| `/aivaconfig` (alias `/aiva`) | Open the configuration window |
| `/cancelspeech` | Stop current speech and flush the queue |
| `/toggletts` | Toggle the plugin on/off |
| `/enabletts` / `/disabletts` | Enable/disable explicitly |
| `/aivapreset <name>` | Apply a named channel preset |
| `/aivavolume <0-200>` | Set master volume; `+N` / `-N` adjust relatively |
| `/aivastyles` | Open the style-tags window |

## Configuration

`/aivaconfig` has seven tabs, mirroring TextToTalk's layout plus engine controls:

1. **Speech Settings** — keybind, source toggles, engine (model status, execution-provider override, default exaggeration), stutter removal.
2. **Models** — per-asset download rows with progress, open models/voices folders.
3. **Player Voices** — per-player voice table with test button and exaggeration bias.
4. **NPC Voices** — the same for NPCs by name.
5. **Channel Settings** — chat-channel presets, per-preset keybinds, enable-all.
6. **Triggers/Exclusions** — text/regex gates on what gets read.
7. **Test** — free-text synthesis with speaker picker, emotion dropdown, exaggeration slider, and rules-vs-context director buttons.

Every capture source, filter, and courtesy behavior can be toggled individually; defaults follow TextToTalk where options are shared. `/aivastyles` opens the style-tag editor (custom styles, tag delimiter, style regex).

Voice assignments persist in `ConfigDirectory/voice-assignments.json`; delete a row there (or via the UI) to re-roll a character.
