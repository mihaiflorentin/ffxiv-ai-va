#!/usr/bin/env python3
"""Assemble voicepacks/aiva-accented from an accented_audition.py run.

  build_accented_pack.py <audition-dir> <stock-voices-dir> <pack-dir>

Reads <audition-dir>/results.json (keep/drop per voice+phonemizer), copies each kept stock
style tensor, cuts a 3 s preview from the audition render, writes voicepack.json and the
audition table (AUDITION.md). KVoiceWalk-made voices are added by extra_voices.json in the
audition dir when present (same shape as a manifest voice, plus "tensor" and "preview" paths).
"""
import json
import shutil
import sys
from pathlib import Path

import numpy as np
import soundfile as sf

audition, stock, pack = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])
data = json.loads((audition / "results.json").read_text())
results = data["results"]

LANGS = {  # stock prefix -> (accent code, display word, tag)
    "j": ("en-x-ja", "Japanese", "japanese"),
    "z": ("en-x-zh", "Chinese", "chinese"),
    "f": ("en-x-fr", "French", "french"),
    "e": ("en-x-es", "Spanish", "spanish"),
    "i": ("en-x-it", "Italian", "italian"),
    "p": ("en-x-pt", "Portuguese", "portuguese"),
    "h": ("en-x-hi", "Hindi", "hindi"),
}
SKIP = {"em_santa", "pm_santa"}   # novelty "Santa Claus" characters, not accent voices
PREVIEW_LINE = 3                  # "Please bring me three bottles of water ..." (plain English, no names)
PREVIEW_SECONDS = 3.0

(pack / "style").mkdir(parents=True, exist_ok=True)
(pack / "preview").mkdir(exist_ok=True)

def best(stock_name):
    """(phonemizer, result) of the best passing combo for a stock voice, else None."""
    passing = {t.split("__")[1]: r for t, r in results.items() if t.startswith(stock_name + "__") and r["keep"]}
    if not passing or stock_name in SKIP:
        return None
    phon = min(passing, key=lambda p: (passing[p]["wer"], passing[p]["mangled"], p != "en-us"))
    return phon, passing[phon]


# Every passing voice is reported, but the pack ships at most one woman and one man per accent
# (the lowest WER of each): a style tensor is 0.5 MB and four near-identical Japanese women help nobody.
stock_names = sorted({t.split("__")[0] for t in results if not t.endswith("__stock")})
shipped = {}
for stock_name in stock_names:
    if (found := best(stock_name)) is not None:
        key = (stock_name[0], stock_name[1])
        if key not in shipped or found[1]["wer"] < best(shipped[key])[1]["wer"]:
            shipped[key] = stock_name

voices, rows = [], []
for stock_name in stock_names:
    for t, r in sorted(results.items()):
        if t.startswith(stock_name + "__"):
            verdict = ("shipped" if shipped.get((stock_name[0], stock_name[1])) == stock_name and t.split("__")[1] == best(stock_name)[0]
                       else "passes, not shipped (a lower-WER option of this accent and sex is shipped)" if r["keep"] else "dropped")
            rows.append((stock_name, t.split("__")[1], r["wer"], r["mangled"], verdict))
    if shipped.get((stock_name[0], stock_name[1])) != stock_name:
        continue
    phon = best(stock_name)[0]
    accent, word, tag = LANGS[stock_name[0]]
    sex = "female" if stock_name[1] == "f" else "male"
    short = f"{accent[-2:]}_{stock_name[1]}_{stock_name.split('_', 1)[1]}"
    shutil.copy(Path(sys.argv[2]) / f"{stock_name}.npy", pack / "style" / f"{short}.npy")
    samples, rate = sf.read(audition / "wav" / f"{stock_name}__{phon}__l{PREVIEW_LINE}.wav", dtype="float32")
    clip = samples[: int(PREVIEW_SECONDS * rate)].copy()
    fade = int(0.15 * rate)
    clip[-fade:] *= np.linspace(1, 0, fade, dtype=np.float32)
    sf.write(pack / "preview" / f"{short}.wav", clip, rate, subtype="PCM_16")
    title = stock_name.split("_", 1)[1].capitalize()
    voices.append({
        "id": f"accented.{short}",
        "name": f"{word}-accented {'woman' if sex == 'female' else 'man'}, {title}",
        "accent": accent,
        "sex": sex,
        "age": "adult",
        "tags": ["accented", tag],
        "description": (
            f"English with {'an' if word[0] in 'AEIOU' else 'a'} {word} accent: Kokoro's stock {word} voice \"{stock_name}\" "
            f"speaking English through the {'British' if phon == 'en-gb' else 'American'} phonemizer."),
        "quality": "experimental",
        "previewFile": f"preview/{short}.wav",
        "realizations": [{"kind": "StyleTensor", "file": f"style/{short}.npy", "phonemizer": phon}],
    })

extra = audition / "extra_voices.json"
if extra.exists():
    for v in json.loads(extra.read_text()):
        shutil.copy(v.pop("tensor"), pack / v["realizations"][0]["file"])
        shutil.copy(v.pop("preview"), pack / v["previewFile"])
        voices.append(v)

manifest = {
    "schema": 1,
    "id": "aiva-accented",
    "version": "1.0.0",
    "name": "Accented English",
    "license": {
        "spdx": "Apache-2.0",
        "attribution": "Style tensors from hexgrad/Kokoro-82M (Apache-2.0), re-registered with an English phonemizer.",
        "url": "https://huggingface.co/hexgrad/Kokoro-82M",
    },
    "voices": voices,
}
(pack / "voicepack.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n")

lines = [
    "| Stock voice | Phonemizer | WER % | Mangled words | Verdict |",
    "|---|---|---|---|---|",
] + [f"| {b} (baseline) | stock | {results[b + '__stock']['wer']} | {results[b + '__stock']['mangled']} | reference |"
     for b in ("af_heart", "am_michael", "bf_emma")] + [f"| {a} | {b} | {c} | {d} | {e} |" for a, b, c, d, e in rows]
(audition / "table.md").write_text("\n".join(lines) + "\n")
print(f"{len(voices)} voices; baseline WER mean {data['baseline_wer_mean']}")
