#!/usr/bin/env python3
"""Cut the reference clip (one whole sentence, at most 10 s) for a cloning engine.

Usage: make_clip.py <dataset.parquet> <speaker_id> <out.wav> <out.txt> [max_seconds=9.5]

Takes the longest sentence of the speaker that is at most max_seconds after silence trimming
(a longer clip carries more of the voice), resampled to 24 kHz mono, peak-normalised, plus its
text. Run with the KVoiceWalk venv python.
"""
import io
import pathlib
import sys

import librosa
import numpy as np
import pyarrow.parquet as pq
import soundfile as sf

RATE = 24000


def main() -> None:
    if len(sys.argv) not in (5, 6):
        raise SystemExit(__doc__)
    source, speaker = sys.argv[1], int(sys.argv[2])
    out_wav, out_txt = pathlib.Path(sys.argv[3]), pathlib.Path(sys.argv[4])
    limit = float(sys.argv[5]) if len(sys.argv) == 6 else 9.5

    best = None
    for row in pq.read_table(source, filters=[("speaker_id", "=", speaker)]).to_pylist():
        samples, rate = sf.read(io.BytesIO(row["audio"]["bytes"]), dtype="float32")
        if samples.ndim > 1:
            samples = samples.mean(axis=1)
        samples = librosa.resample(samples, orig_sr=rate, target_sr=RATE)
        samples, _ = librosa.effects.trim(samples, top_db=35)
        seconds = len(samples) / RATE
        if seconds <= limit and (best is None or seconds > best[0]):
            best = (seconds, samples, row["text"].strip().rstrip(".") + ".")
    if best is None:
        raise SystemExit(f"speaker {speaker} has no sentence of {limit} s or less")

    seconds, samples, text = best
    samples = np.concatenate([np.zeros(int(0.05 * RATE), dtype=np.float32), samples, np.zeros(int(0.1 * RATE), dtype=np.float32)])
    samples = 0.9 * samples / (float(np.max(np.abs(samples))) or 1.0)
    out_wav.parent.mkdir(parents=True, exist_ok=True)
    sf.write(out_wav, samples, RATE, subtype="PCM_16")
    out_txt.write_text(text + "\n", encoding="utf-8")
    print(f"{seconds:.1f} s: {text}")


if __name__ == "__main__":
    main()
