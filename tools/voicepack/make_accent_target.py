#!/usr/bin/env python3
"""Build a KVoiceWalk target (wav + txt, <=28 s, 24 kHz) for one speaker of the
westbrook/English_Accent_DataSet parquet (VoxPopuli rows are CC0).

  make_accent_target.py <parquet> <accent-label-int> <speaker_id> <out.wav>   -> prints median f0
Accent labels: 0 Dutch, 1 German, 4 French, 9 Spanish, 10 Italian (see the parquet schema).
"""
import io
import sys

import librosa
import numpy as np
import pyarrow.parquet as pq
import soundfile as sf

src, accent, speaker, out = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]
rows = [r for r in pq.read_table(src).to_pylist() if r["accent"] == accent and r["speaker_id"] == speaker]
rows.sort(key=lambda r: abs(r["duration"] - 12))   # clips near 12 s: enough speech, not a whole speech
gap = np.zeros(int(0.35 * 24000), dtype=np.float32)
parts, texts, total = [], [], 0.0
for r in rows:
    y, sr = sf.read(io.BytesIO(r["audio"]["bytes"]), dtype="float32")
    if y.ndim > 1:
        y = y.mean(axis=1)
    y = librosa.resample(y, orig_sr=sr, target_sr=24000)
    y, _ = librosa.effects.trim(y, top_db=35)
    if total + len(y) / 24000 > 28:
        continue
    parts += [y, gap]
    texts.append(r["raw_text"].strip().capitalize().rstrip(".") + ".")
    total += len(y) / 24000 + 0.35
    if total > 24:
        break
audio = np.concatenate(parts)
audio = 0.9 * audio / np.max(np.abs(audio))
sf.write(out, audio, 24000, subtype="PCM_16")
open(out.replace(".wav", ".txt"), "w").write(" ".join(texts))
f0 = librosa.pyin(audio, fmin=60, fmax=400, sr=24000)[0]
print(f"{total:.1f}s median f0 {np.nanmedian(f0):.0f} Hz")
print(" ".join(texts))
