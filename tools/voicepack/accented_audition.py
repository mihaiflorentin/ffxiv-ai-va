#!/usr/bin/env python3
"""Audition Kokoro's stock non-English voices speaking English (pack aiva-accented).

Renders fixed English test lines through the SmokeSynth kokoro engine with each stock
non-English style tensor re-registered under an English phonemizer (a* = en-us, b* = en-gb),
transcribes them with faster-whisper and compares the word error rate (WER) against stock
English baseline voices. A voice/phonemizer combo is kept only if it stays within a margin
of the baseline (see MARGIN_POINTS and the mangled-word rule below).

  accented_audition.py render   # needs a built tools/SmokeSynth (Release) and ~/.cache/aiva-kokoro
  accented_audition.py score    # needs the KVoiceWalk venv (faster-whisper) and a small.en model

Paths come from environment variables so the script has no machine-specific defaults:
  AIVA_SMOKE_DLL   path to SmokeSynth.dll         AIVA_KOKORO_MODELS  dir with kokoro-v1.0.onnx
  AIVA_STOCK_VOICES  dir with the stock *.npy     AIVA_AUDITION_OUT   output dir for wavs/results
  AIVA_WHISPER_ROOT  faster-whisper download root (score only)
"""
import difflib
import itertools
import json
import os
import re
import shutil
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

LINES = [
    "The Warrior of Light has arrived in Ul'dah, and Y'shtola is waiting at the Rising Stones.",
    "I never thought we would see Gridania again, but the forest remembers everyone who walks beneath its trees.",
    "Alphinaud, Alisaie and Thancred stood before Garlemald while the airship Enterprise drifted slowly through the evening clouds.",
    "Please bring me three bottles of water and a loaf of bread before the market closes this afternoon.",
    "Warriors of Light, we thrive where others vanish, and every river remembers the first villager who crossed it.",
    "Thank you for the quest, Minfilia. I will deliver the weapons to the Immortal Flames before the third bell rings.",
    "Whether the weather is rough or the water is calm, the wizard will vividly wander through the valley.",
    "Nobody expected that the Crystal Exarch would sacrifice his entire library to rescue the children of Ishgard.",
]
# Proper nouns the ASR cannot be expected to spell; they are excluded from the mangled-word check.
NAMES = {"uldah", "yshtola", "gridania", "alphinaud", "alisaie", "thancred", "garlemald", "enterprise", "minfilia", "exarch", "ishgard"}
WEAK_WORDS = {"the", "a", "and", "of"}  # ASR drops or merges these without any mispronunciation
BASELINES = ["af_heart", "am_michael", "bf_emma"]
PHONEMIZERS = {"en-us": "a", "en-gb": "b"}
MARGIN_POINTS = 5.0       # accepted WER <= baseline mean + 5 points
MAX_EXTRA_MANGLED = 0     # and no content word (non-name) heard wrongly beyond what the worst baseline shows
ACCENTED_PREFIXES = "jzfeiph"


def stock_voices(stock_dir: Path):
    return sorted(p.stem for p in stock_dir.glob("*.npy") if p.stem[0] in ACCENTED_PREFIXES and p.stem[2] == "_")


def temp_name(prefix: str, stock: str) -> str:
    return f"{prefix}{stock[1]}_acc_{stock}"


def render(out: Path, stock_dir: Path, smoke_dll: Path, models: Path):
    voices_dir = smoke_dll.parent / "voices"
    targets = []  # (voice name used by Kokoro, wav path, text)
    for stock in stock_voices(stock_dir):
        for phon, prefix in PHONEMIZERS.items():
            name = temp_name(prefix, stock)
            shutil.copy(stock_dir / f"{stock}.npy", voices_dir / f"{name}.npy")
            targets.append((name, f"{stock}__{phon}"))
    targets += [(b, f"{b}__stock") for b in BASELINES]
    jobs = [(name, tag, i, text) for (name, tag), (i, text) in itertools.product(targets, enumerate(LINES))]

    def run(job):
        name, tag, i, text = job
        wav = out / "wav" / f"{tag}__l{i}.wav"
        if wav.exists():
            return
        wav.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run(
            ["dotnet", str(smoke_dll), "--engine", "kokoro", "--text", text, "--ref", name,
             "--models", str(models), "--out", str(wav)],
            check=True, capture_output=True)

    try:
        with ThreadPoolExecutor(max_workers=3) as pool:
            list(pool.map(run, jobs))
    finally:
        for stock in stock_voices(stock_dir):
            for prefix in PHONEMIZERS.values():
                (voices_dir / f"{temp_name(prefix, stock)}.npy").unlink(missing_ok=True)


def norm(text: str):
    text = text.lower().replace("'", "").replace("’", "")
    words = re.findall(r"[a-z0-9]+", text)
    return ["three" if w == "3" else w for w in words]   # the ASR writes some numbers as digits


def edit_distance(ref, hyp):
    d = list(range(len(hyp) + 1))
    for i, r in enumerate(ref, 1):
        prev, d[0] = d[0], i
        for j, h in enumerate(hyp, 1):
            prev, d[j] = d[j], min(d[j] + 1, d[j - 1] + 1, prev + (r != h))
    return d[len(hyp)]


def mangled_words(ref, hyp):
    """Content words (not names, not the weakest function words) that the alignment shows heard as something else."""
    count = 0
    for op, a1, a2, _, _ in difflib.SequenceMatcher(None, ref, hyp, autojunk=False).get_opcodes():
        if op != "equal":
            count += sum(1 for w in ref[a1:a2] if w not in NAMES and w not in WEAK_WORDS)
    return count


def score(out: Path, whisper_root: Path):
    import librosa
    import soundfile
    from faster_whisper import WhisperModel
    model = WhisperModel("small.en", device="cpu", compute_type="int8", cpu_threads=4,
                         download_root=str(whisper_root))
    tags = sorted({p.name.rsplit("__l", 1)[0] for p in (out / "wav").glob("*.wav")})
    cache_file = out / "scores-cache.json"   # transcribing is slow; make the run resumable
    cache = json.loads(cache_file.read_text()) if cache_file.exists() else {}
    results = {}
    for tag in tags:
        if tag in cache:
            results[tag] = cache[tag]
            continue
        errors = words = mangled = 0
        transcripts = []
        for i, text in enumerate(LINES):
            samples, rate = soundfile.read(str(out / "wav" / f"{tag}__l{i}.wav"), dtype="float32")
            samples = librosa.resample(samples, orig_sr=rate, target_sr=16000)  # decode here: PyAV in the venv is too old
            segments, _ = model.transcribe(samples, language="en",
                                           beam_size=5, condition_on_previous_text=False)
            hyp_text = " ".join(s.text.strip() for s in segments)
            ref, hyp = norm(text), norm(hyp_text)
            errors += edit_distance(ref, hyp)
            words += len(ref)
            mangled += mangled_words(ref, hyp)
            transcripts.append(hyp_text)
        results[tag] = {"wer": round(100.0 * errors / words, 1), "mangled": mangled, "transcripts": transcripts}
        print(tag, results[tag]["wer"], results[tag]["mangled"], flush=True)
        cache[tag] = results[tag]
        cache_file.write_text(json.dumps(cache))
    base = [results[f"{b}__stock"] for b in BASELINES]
    base_wer = sum(b["wer"] for b in base) / len(base)
    base_mangled = max(b["mangled"] for b in base)
    for tag, r in results.items():
        r["keep"] = (not tag.endswith("__stock")
                     and r["wer"] <= base_wer + MARGIN_POINTS
                     and r["mangled"] <= base_mangled + MAX_EXTRA_MANGLED)
    (out / "results.json").write_text(json.dumps(
        {"baseline_wer_mean": round(base_wer, 1), "baseline_mangled_max": base_mangled, "results": results}, indent=1))
    print(f"baseline WER mean {base_wer:.1f}, worst baseline mangled {base_mangled}")


if __name__ == "__main__":
    env = os.environ
    out = Path(env["AIVA_AUDITION_OUT"])
    if sys.argv[1] == "render":
        render(out, Path(env["AIVA_STOCK_VOICES"]), Path(env["AIVA_SMOKE_DLL"]), Path(env["AIVA_KOKORO_MODELS"]))
    else:
        score(out, Path(env["AIVA_WHISPER_ROOT"]))
