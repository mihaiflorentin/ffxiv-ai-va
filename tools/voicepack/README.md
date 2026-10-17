# tools/voicepack - voice pack generator

Reproducible pipeline that turns a public speech dataset into a plugin voice pack
(`voicepack.json` schema 1). The bundled `aiva-isles` pack (Irish, Scottish, Welsh,
Northern, Midlands and Southern English accents) is built with it.

```
dataset (parquet) -> target clip (~25 s) -> KVoiceWalk random walk -> .pt -> style/<name>.npy
                          |                                                      |
                          +-> reference clip (one sentence, <= 10 s)              +-> preview/<name>.wav (SmokeSynth)
                                                  all of it -> voicepack.json
```

Files:

| File | Role |
|---|---|
| `isles.voices.json` | Generator spec: pack metadata and one entry per voice (dataset config, speaker id, accent, sex, tags). The only hand-edited input. |
| `build_voice.py` | Orchestrator. One command per voice; stages `fetch,target,walk,tensor,clip,manifest,preview`. |
| `fetch_dataset.py` | Downloads one `ylacombe/english_dialects` config (length-checked, retried) and can list speakers. |
| `make_target.py` | Cuts a ~12-25 s, 24 kHz target clip, its transcript and a per-sentence timing file for one speaker. |
| `make_clip.py` | Cuts the reference clip: the speaker's longest single sentence of at most 9.5 s, plus its text. |
| `pt_to_npy.py` | KVoiceWalk `.pt` to the `(510, 1, 256)` float32 `.npy` the plugin reads. |
| `isles.scores.json` | Written by the `tensor` stage: step of the last improvement, fitness score and speaker similarity (resemblyzer, vs. one dataset speaker) of each shipped tensor. |
| `gen_core_manifest.py` | Unrelated: regenerates the stock Kokoro manifest `Domain/voices-core.json`. |

## One-time setup

Everything heavy lives in a work directory that is never committed. The default is
`models/kvoicewalk-work` (`models/` is gitignored); override with `AIVA_VOICEWALK_WORK` or
`--work`. A git worktree finds the work directory of the checkout it hangs off.

```bash
W=models/kvoicewalk-work
mkdir -p $W && cd $W
git clone https://github.com/RobViren/kvoicewalk.git kvoicewalk      # Apache-2.0
cd kvoicewalk
uv venv --python 3.12 && uv sync                                      # CPU torch wheels (see its pyproject)
.venv/bin/python -m pip install 'setuptools<81'                       # resemblyzer imports pkg_resources
```

KVoiceWalk was written against older numpy/librosa; the venv used for the shipped pack
needed small compatibility edits in `utilities/` (numpy 2.x typing imports, librosa
keyword-only arguments). If a walk crashes on startup, read the traceback and patch the
call it names; the scoring logic is untouched.

Other requirements: Python 3 (standard library) to run `build_voice.py`; the `preview`
stage needs the .NET SDK and a Kokoro model directory (default `~/.cache/aiva-kokoro`,
containing `kokoro-v1.0.onnx`; the plugin's Models tab downloads the same file). Network access
to `huggingface.co` for the dataset.

## Regenerate one voice

```bash
python3 tools/voicepack/build_voice.py isles.sc_m_01            # all stages, 600 walk steps (spec default)
```

That runs, in order: download the Scottish-male dataset, build the target clip for the
speaker in the spec, run the walk (`<work>/logs/sc_m_01.log`), convert the best tensor to
`src/AIVoiceActing/voicepacks/aiva-isles/style/sc_m_01.npy`, cut `clips/sc_m_01.wav` + `.txt`,
rewrite `voicepack.json`, and render `preview/sc_m_01.wav` with `SmokeSynth --audition`.

Useful variants:

```bash
# Walk longer / continue: a later walk starts from the best tensor of earlier ones.
python3 tools/voicepack/build_voice.py isles.sc_m_01 --stages walk,tensor,clip,manifest,preview --steps 1500
# Start over with a different speaker: edit "speaker" in the spec, then
python3 tools/voicepack/build_voice.py isles.sc_m_01 --fresh --force
# Only the cheap stages for the whole pack (after editing names/tags/description):
python3 tools/voicepack/build_voice.py --all --stages manifest
# Pick a speaker: lists speaker ids with utterance counts
<work>/kvoicewalk/.venv/bin/python -I tools/voicepack/fetch_dataset.py scottish_male <work>/data --speakers
```

A walk takes about 4-5 s per step on a free 16-core CPU (`--threads`, default 3) after a
roughly ten minute warm-up that scores KVoiceWalk's seed voices. Several voices can walk at
once; four at four threads each suits a 16-core machine. `build_voice.py` is safe to run in
parallel for different voices (the scores file is locked).

## Walking on a GPU (ROCm or CUDA)

On a loaded CPU a step takes 15-20 s; on a Radeon RX 9070 XT (gfx1201) it takes about 2 s. Make a second
venv with a GPU build of torch and give it the KVoiceWalk packages by path:

```bash
uv venv --python 3.12 models/rocm-venv
uv pip install --python models/rocm-venv/bin/python --index-url https://download.pytorch.org/whl/rocm7.0 torch
echo "$PWD/models/kvoicewalk-work/kvoicewalk/.venv/lib/python3.12/site-packages" \
  > models/rocm-venv/lib/python3.12/site-packages/kvw_old.pth       # the CPU venv's other packages
printf 'import torch\ntorch.backends.cudnn.enabled = False\n' \
  > models/rocm-venv/lib/python3.12/site-packages/sitecustomize.py   # MIOpen LSTM fails on gfx1201
python3 tools/voicepack/build_voice.py isles.sc_m_01 --stages walk --device cuda \
  --walk-python models/rocm-venv/bin/python --threads 2 --steps 800
```

(ROCm exposes itself as `cuda`.) Sandboxed shells hide `/dev/kfd` and `/dev/dri`; run it unsandboxed.
`touch <work>/PAUSE` holds back new CPU walks (GPU walks ignore it).

## Quality gate

```bash
dotnet run --project tools/SmokeSynth -c Release -p:UseAppHost=false -p:AllowMissingPrunePackageData=true -- \
  --validate-pack src/AIVoiceActing/voicepacks/aiva-isles
dotnet run --project tools/SmokeSynth -c Release -p:UseAppHost=false -p:AllowMissingPrunePackageData=true -- \
  --audition src/AIVoiceActing/voicepacks/aiva-isles --out /some/dir --models ~/.cache/aiva-kokoro
```

`--validate-pack` uses the plugin's own manifest parser and library scan; it exits 1 when a
referenced file is missing or malformed, a clip exceeds 10 s, or the manifest is rejected.
Missing LICENSE/NOTICE files are warnings. `--audition` renders each voice's preview line;
listen to all of them before raising a voice from `experimental`.

## Choices worth knowing

- **Phonemizer.** KVoiceWalk scores against Kokoro's American G2P, so tensors are paired with
  `"phonemizer": "en-us"` (the spec default; override per voice). A check on the first Irish
  voice (same line rendered with each phonemizer, speaker similarity to the target clip) gave
  0.77 for en-us against 0.74 for en-gb, so en-us stays.
- **Similarity is not accent.** The score is a speaker-embedding similarity to one real
  speaker; it tracks timbre more than dialect. Treat every output as `experimental` until a
  person has listened.
- **Reference clips** are whole sentences from the target, at most 10 s, for cloning engines
  (Chatterbox Turbo, F5) that take a clip plus transcript instead of a tensor.
- **License.** The datasets are CC-BY-SA-4.0, so tensors, clips and previews are derivatives
  and the pack ships with its own LICENSE and NOTICE as a separate data collection. Keep
  those files and the attribution in `isles.voices.json` when adding voices. There is no
  `irish_female` config in the dataset, so the pack has no Irish female voice.
- **Dataset fetch.** HuggingFace's parquet mirror occasionally cuts a download short without
  an error; `fetch_dataset.py` compares the byte count and retries.
