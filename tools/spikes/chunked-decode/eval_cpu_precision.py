#!/usr/bin/env python3
"""
Reproduces the "which precision for the CPU graphs" and "prompt length" tables of
docs/plans/2026-10-gpu-spike-results.md (issue #23).

For every line it decodes the speech tokens the real language model produced with each combination of
speech-encoder precision (the reference prompt) and conditional-decoder precision on the CPU, transcribes the
result with faster-whisper and reports the mean word error rate and the speaker cosine to the reference clip
(speech-encoder x-vector of the output against that of the reference).

Inputs:
  --models   folder with the Turbo files under their catalog local names (turbo-speech-encoder*.onnx, ...)
  --lines    text file, one line of text per line
  --tokens   folder with s-1.tokens, s-2.tokens, ... (comma separated speech tokens; write them with
             SmokeSynth --engine turbo --device gpu --precision q4f16 --lines <file> --out <dir>/s.wav)
  --whisper  folder of a faster-whisper model (for example the small.en snapshot)

Run with a Python that has onnxruntime, numpy, librosa, soundfile and faster-whisper, for example
models/kvoicewalk-work/kvoicewalk/.venv/bin/python (run it with -I when the inputs are untrusted).
"""
import argparse
import re
import numpy as np
import onnxruntime as ort
import librosa
from faster_whisper import WhisperModel

SILENCE = 4299  # the three closing silence tokens the plugin appends


def graph(models, kind, precision):
    return f"{models}/turbo-{kind}" + ("" if precision == "fp32" else "-" + precision) + ".onnx"


def session(path, threads=4):
    options = ort.SessionOptions()
    options.intra_op_num_threads = threads
    return ort.InferenceSession(path, options, providers=["CPUExecutionProvider"])


def words(text):
    return re.sub(r"[^a-z' ]", " ", text.lower()).split()


def wer(reference, hypothesis):
    r, h = words(reference), words(hypothesis)
    row = list(range(len(h) + 1))
    for i in range(1, len(r) + 1):
        previous, row[0] = row[0], i
        for j in range(1, len(h) + 1):
            current = row[j]
            row[j] = min(row[j] + 1, row[j - 1] + 1, previous + (r[i - 1] != h[j - 1]))
            previous = current
    return row[len(h)] / max(1, len(r))


def cosine(a, b):
    a, b = a.ravel(), b.ravel()
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b)))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--models", required=True)
    parser.add_argument("--lines", required=True)
    parser.add_argument("--tokens", required=True)
    parser.add_argument("--whisper", required=True)
    parser.add_argument("--voice", help="reference clip (default: turbo-default-voice.wav in --models)")
    parser.add_argument("--prompt-tails", default="", help="comma separated prompt lengths to test with q4f16 everywhere, for example 100,80,60")
    args = parser.parse_args()

    voice = args.voice or f"{args.models}/turbo-default-voice.wav"
    audio, _ = librosa.load(voice, sr=24000, mono=True)
    lines = [l.strip() for l in open(args.lines) if l.strip()]
    tokens = [np.array([int(x) for x in open(f"{args.tokens}/s-{i + 1}.tokens").read().split(",")], dtype=np.int64) for i in range(len(lines))]
    whisper = WhisperModel(args.whisper, device="cpu", compute_type="int8", cpu_threads=4)

    encoders = {p: session(graph(args.models, "speech-encoder", p)).run(None, {"audio_values": audio[None].astype(np.float32)}) for p in ("fp32", "fp16", "q4f16")}
    decoders = {p: session(graph(args.models, "conditional-decoder", p)) for p in ("fp32", "fp16", "q4f16")}
    reference_xvector = encoders["fp32"][2]
    xvector_session = session(graph(args.models, "speech-encoder", "fp32"))

    def evaluate(label, decoder, encoder, keep=None):
        _, prompt_tokens, xvector, prompt_feat = encoders[encoder]
        errors, likeness = [], []
        for text, speech in zip(lines, tokens):
            head = prompt_tokens[0] if keep is None else prompt_tokens[0][-keep:]
            feat = prompt_feat if keep is None else prompt_feat[:, -2 * keep:, :]
            stream = np.concatenate([head, speech, [SILENCE] * 3])[None]
            wav = decoders[decoder].run(None, {"speech_tokens": stream, "speaker_embeddings": xvector, "speaker_features": feat})[0][0]
            heard = librosa.resample(wav.astype(np.float32), orig_sr=24000, target_sr=16000)
            segments, _ = whisper.transcribe(heard, language="en", beam_size=5)
            errors.append(wer(text, " ".join(s.text.strip() for s in segments)))
            likeness.append(cosine(xvector_session.run(None, {"audio_values": wav[None].astype(np.float32)})[2], reference_xvector))
        print(f"{label:34s} mean WER {np.mean(errors):.3f}  speaker cosine {np.mean(likeness):.4f}", flush=True)

    print("== encoder (prompt) x decoder precision")
    for encoder, decoder in (("q4f16", "q4f16"), ("fp32", "fp32"), ("fp32", "q4f16"), ("fp16", "fp16"), ("q4f16", "fp32")):
        evaluate(f"encoder {encoder} / decoder {decoder}", decoder, encoder)
    if args.prompt_tails:
        print("== prompt tail kept (q4f16 everywhere)")
        for keep in [int(x) for x in args.prompt_tails.split(",")]:
            evaluate(f"prompt tail {keep}", "q4f16", "q4f16", keep)


if __name__ == "__main__":
    main()
