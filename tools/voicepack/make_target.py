#!/usr/bin/env python3
"""Build a KVoiceWalk target clip (~25 s, 24 kHz mono) for one dataset speaker.

Usage: make_target.py <dataset.parquet> <speaker_id> <out.wav> [goal_seconds=24]

Writes <out.wav>, <out>.txt (the transcript KVoiceWalk compares against) and <out>.json
(per-utterance start/end/text, used later to cut a reference clip on a sentence boundary).
Sentences are added until the clip passes goal_seconds (the final clip is at most goal+4 s).
A shorter goal makes every KVoiceWalk step faster (it re-synthesizes the whole target text each
step) at the price of a thinner target. Longer sentences are preferred because they carry more of the accent. Run it with the
KVoiceWalk venv python (needs pyarrow, soundfile, librosa, numpy).
"""
import io
import json
import pathlib
import sys

import librosa
import numpy as np
import pyarrow.parquet as pq
import soundfile as sf

RATE = 24000
GAP_SECONDS = 0.35
DEFAULT_GOAL_SECONDS = 24.0


def main() -> None:
    if len(sys.argv) not in (4, 5):
        raise SystemExit(__doc__)
    source, speaker, out = sys.argv[1], int(sys.argv[2]), pathlib.Path(sys.argv[3])
    goal = float(sys.argv[4]) if len(sys.argv) == 5 else DEFAULT_GOAL_SECONDS
    limit = goal + 4.0

    rows = pq.read_table(source, filters=[("speaker_id", "=", speaker)]).to_pylist()
    if not rows:
        raise SystemExit(f"speaker {speaker} not found in {source}")
    rows.sort(key=lambda r: -len(r["text"]))  # stable: ties keep the dataset order

    gap = np.zeros(int(GAP_SECONDS * RATE), dtype=np.float32)
    parts, segments, cursor = [], [], 0.0
    for row in rows:
        samples, rate = sf.read(io.BytesIO(row["audio"]["bytes"]), dtype="float32")
        if samples.ndim > 1:
            samples = samples.mean(axis=1)
        samples = librosa.resample(samples, orig_sr=rate, target_sr=RATE)
        samples, _ = librosa.effects.trim(samples, top_db=35)
        seconds = len(samples) / RATE
        if cursor + seconds > limit:
            continue
        text = row["text"].strip().rstrip(".") + "."
        segments.append({"start": round(cursor, 3), "end": round(cursor + seconds, 3), "text": text})
        parts += [samples, gap]
        cursor += seconds + GAP_SECONDS
        if cursor > goal:
            break

    audio = np.concatenate(parts)
    peak = float(np.max(np.abs(audio))) or 1.0
    audio = 0.9 * audio / peak
    out.parent.mkdir(parents=True, exist_ok=True)
    sf.write(out, audio, RATE, subtype="PCM_16")
    out.with_suffix(".txt").write_text(" ".join(s["text"] for s in segments), encoding="utf-8")
    out.with_suffix(".json").write_text(json.dumps(segments, indent=1), encoding="utf-8")
    print(f"{cursor:.1f}s, {len(segments)} utterances -> {out}")


if __name__ == "__main__":
    main()
