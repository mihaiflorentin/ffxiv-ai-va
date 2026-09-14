# AIVoiceActing

Local AI voice acting for FFXIV. The plugin captures quest Talk dialogue, BattleTalk, cutscene lines, and chat channels, and speaks each line aloud with a locally-running neural TTS model (**Kokoro**, ONNX). No cloud services, no Python sidecar — inference runs in-process through ONNX Runtime.

- Every character gets a **persistent voice**: a speaker's first line picks randomly from their race/gender row and the pick is pinned in `voice-assignments.json` forever. Manual per-NPC and per-player overrides win.
- **Lore-faithful casting**: every playable race maps to curated voice sets — French-accented for Elezen, Chinese bank for Au Ra, pitched British voices for Lalafell, Italian for Roegadyn — with multiple voices per race/gender so crowds vary. Beast tribes get their own rows (pool + male/female lists), bound to in-game model ids.
- **Per-voice control**: the General Voices tab shapes every row directly (voice picker, pitch/speed/volume/exaggeration per slot); the NPC Voices and Player Voices tabs override individual characters, with add forms, scoped clear, and clipboard export/import.
- **Delivery is shaped by context**: a rules-based emotion director (zero cost, always on) sets exaggeration, pacing, and paralinguistic style per line; an optional small LLM director asset can read the recent cutscene dialogue for finer direction.
- **The game's own voice acting always wins**: when a voiced line plays in-game, the plugin cancels its speech and stays silent for that line.

Kokoro's accent banks cover English (US/UK), French, Spanish, Italian, Portuguese, Hindi, and Chinese. The Japanese bank is parked (it does not render English); f5, turbo, and legacy Chatterbox engines remain in the code but are hidden from the UI.


## Requirements

- Windows 10/11 for in-game use (FFXIV + Dalamud via XIVLauncher). The audio sink (WASAPI) and the DirectML execution provider are Windows-only.
- .NET 10 desktop runtime, installed by XIVLauncher/Dalamud.
- ~350 MB of model files for the default Kokoro engine, downloaded once from the **Engine** tab on first use (download only starts when you press the button). The parked f5/turbo engines add ~1.4 GB / ~1.1 GB if ever enabled.
- macOS: the engine runs for local development and XIV on Mac builds load with the CPU execution provider — see [GPU notes](#gpu-notes).

## Models and licenses

| Asset | Source | License |
|---|---|---|
| Kokoro v1.0 ONNX model (`kokoro-v1.0.onnx`, ~310 MB) | [Kokoro](https://huggingface.co/hexgrad/kokoro) (KokoroSharp ships the export) | Apache-2.0 |
| Voice bank (`voices/*.npy`, 54 voices) | Kokoro v1.0/v1.1 style vectors, shipped with the plugin | Apache-2.0 |
| Optional emotion-director LLM (Qwen3-0.6B ONNX; not required, off by default) | Qwen3 | Apache-2.0 |
| Legacy Chatterbox assets (engine parked; only if manually enabled) | [`onnx-community/chatterbox-ONNX`](https://huggingface.co/onnx-community/chatterbox-ONNX) | MIT |

Models are downloaded to `ConfigDirectory/models/` at runtime; nothing model-sized ships in the plugin zip.

## Install

The repository must be public — the plugin installer and the manual download link below fetch files unauthenticated.

**Option A — plugin repository (automatic updates):**

1. In XIVLauncher, open **Settings → Dalamud** and add this URL under **Custom Plugin Repositories**:

    ```
    https://raw.githubusercontent.com/mihaiflorentin/ffxiv-ai-va/gh-pages/repo.json
    ```

2. Open the plugin installer, find **AI Voice Acting**, and install it.

**Option B — manual:**

Download `latest.zip` from the [releases page](https://github.com/mihaiflorentin/ffxiv-ai-va/releases) — direct link: <https://github.com/mihaiflorentin/ffxiv-ai-va/releases/latest/download/latest.zip> — and extract it into `%APPDATA%\XIVLauncher\devPlugins\AIVoiceActing\` so that folder directly contains `AIVoiceActing.dll`.

**First use:** launch the game, run `/aivaconfig`, open the **Models** tab, and press **Download** (~1.1 GB, one time; the button is the only trigger). Voices start working immediately after the download finishes.

## Building from source

Requires the .NET 10 SDK and a Dalamud development checkout for assembly references.

```sh
git clone https://github.com/mihaiflorentin/ffxiv-ai-va.git
cd ffxiv-ai-va

# Dalamud reference assemblies (also used by CI):
curl -fsSL https://goatcorp.github.io/dalamud-distrib/latest.zip -o dalamud.zip
unzip -q dalamud.zip -d .dalamud/dev

export DALAMUD_HOME="$PWD/.dalamud/dev"

dotnet build AIVoiceActing.sln -c Release
dotnet test                       # 441 unit tests
dotnet publish src/AIVoiceActing -c Release
```

`dotnet publish` produces `src/AIVoiceActing/bin/Release/AIVoiceActing/latest.zip` — the shippable plugin archive (plugin dll + deps + manifest + icon + `voices/` + required natives).

CI (`.github/workflows/release.yml`) mirrors this: it builds on every push/PR, runs the test suite, and on a `v*` tag publishes `latest.zip` to a GitHub release and `repo.json` to the `gh-pages` branch (served via `raw.githubusercontent.com`; no Pages setup required).

## GPU notes

Synthesis runs through ONNX Runtime execution providers, on a background thread — the game thread is never blocked.

- **Kokoro is real-time on CPU**: measured real-time factor ≈ 0.54 on a Ryzen 9800X3D (1 s of audio in ~0.5 s); older CPUs still keep up with dialogue pacing.
- **DirectML stays CPU under Wine**: the DML EP fails in-game; the code falls back to CPU automatically. The execution-provider selector remains in the Engine tab for native Windows experiments.
- **macOS** (XIV on Mac): CPU is the default and works.
- The parked f5/turbo engines are an order of magnitude slower (F5 ≈ 8× real time on CPU) — why they stay out of the UI.

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

`/aivaconfig` has four tabs:

1. **Engine** — the engine card (Kokoro), model download/remove per asset with progress and cancel, execution-provider selector, load-engine-now button, and a live status strip (state, queue depth, models-dir size).
2. **Voices** — Players and NPCs tables: voice picker, exaggeration bias, per-voice volume slider, test button, and add-form.
3. **Chat** — master enable, keybinds, capture sources, channel presets, and triggers/exclusions.
4. **Test** — free-text synthesis with speaker picker, emotion dropdown, forced-emotion audition, and rules-vs-context director buttons; failures surface in an in-window status line.

Every capture source, filter, and courtesy behavior can be toggled individually; defaults follow TextToTalk where options are shared. `/aivastyles` opens the style-tag editor (custom styles, tag delimiter, style regex).

Voice assignments persist in `ConfigDirectory/voice-assignments.json`; delete a row there (or via the UI) to re-roll a character.
