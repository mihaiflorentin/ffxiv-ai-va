#!/usr/bin/env python3
"""Reference server for the AIVoiceActing "Remote server" engine.

Speaks the OpenAI-style endpoint the plugin calls:

    POST /v1/audio/speech
    {"model": "...", "input": "text", "voice": "af_heart",
     "response_format": "wav", "speed": 1.0, "reference_wav": "<base64, optional>"}

and answers with a mono 16-bit wav. Standard library only for the stub backend; the
kokoro backend needs `pip install kokoro numpy torch` (see README.md).
"""

import argparse
import base64
import io
import json
import math
import struct
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def wav_bytes(samples, sample_rate):
    """Pack floats in [-1, 1] as a mono 16-bit PCM wav."""
    pcm = b"".join(
        struct.pack("<h", max(-32768, min(32767, int(s * 32767)))) for s in samples
    )
    header = b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVE"
    header += b"fmt " + struct.pack("<IHHIIHH", 16, 1, 1, sample_rate, sample_rate * 2, 2, 16)
    header += b"data" + struct.pack("<I", len(pcm))
    return header + pcm


class StubBackend:
    """No model: a short tone whose length follows the text. For wiring checks."""

    sample_rate = 24000

    def synthesize(self, text, voice, speed, reference_wav):
        seconds = max(0.3, min(6.0, len(text) * 0.06 / max(speed, 0.1)))
        count = int(self.sample_rate * seconds)
        freq = 180 + (sum(map(ord, voice)) % 120)
        return [0.3 * math.sin(2 * math.pi * freq * i / self.sample_rate) for i in range(count)]


class KokoroBackend:
    """Kokoro-82M through the `kokoro` pip package. Uses CUDA/ROCm when torch sees a GPU."""

    sample_rate = 24000

    def __init__(self, device):
        import numpy as np
        import torch
        from kokoro import KPipeline

        self.np = np
        self.device = device or ("cuda" if torch.cuda.is_available() else "cpu")
        print(f"kokoro backend on {self.device}", file=sys.stderr)
        self.pipelines = {}
        self.KPipeline = KPipeline

    def _pipeline(self, voice):
        lang = voice[0] if voice and voice[0] in "abefhijpz" else "a"
        if lang not in self.pipelines:
            self.pipelines[lang] = self.KPipeline(lang_code=lang, device=self.device)
        return self.pipelines[lang]

    def synthesize(self, text, voice, speed, reference_wav):
        # Kokoro has no cloning; reference_wav is ignored and the voice id is used as is.
        chunks = [
            audio.cpu().numpy() if hasattr(audio, "cpu") else audio
            for _, _, audio in self._pipeline(voice)(text, voice=voice, speed=speed)
        ]
        return self.np.concatenate(chunks).tolist() if chunks else []


def make_handler(backend, default_voice, token):
    class Handler(BaseHTTPRequestHandler):
        def _send(self, status, body, content_type="application/json"):
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _error(self, status, message):
            self._send(status, json.dumps({"error": message}).encode())

        def do_GET(self):
            if self.path in ("/", "/health", "/v1/models"):
                self._send(200, json.dumps({"status": "ok"}).encode())
            else:
                self._error(404, "not found")

        def do_POST(self):
            if self.path.rstrip("/") != "/v1/audio/speech":
                return self._error(404, "not found")
            if token and self.headers.get("Authorization") != f"Bearer {token}":
                return self._error(401, "bad token")
            try:
                length = int(self.headers.get("Content-Length", "0"))
                body = json.loads(self.rfile.read(length) or b"{}")
                text = str(body["input"]).strip()
            except (ValueError, KeyError):
                return self._error(400, "expected JSON with an 'input' field")
            if not text:
                return self._error(400, "empty input")

            voice = body.get("voice") or default_voice
            speed = float(body.get("speed") or 1.0)
            reference = body.get("reference_wav")
            reference_wav = base64.b64decode(reference) if reference else None
            try:
                samples = backend.synthesize(text, voice, speed, reference_wav)
            except Exception as ex:  # surfaced to the plugin as a 500 with this text
                print(f"synthesis failed: {ex!r}", file=sys.stderr)
                return self._error(500, f"synthesis failed: {ex}")
            if len(samples) == 0:
                return self._error(500, "backend produced no audio")
            self._send(200, wav_bytes(samples, backend.sample_rate), "audio/wav")

        def log_message(self, fmt, *args):
            print("%s - %s" % (self.address_string(), fmt % args), file=sys.stderr)

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--backend", choices=["stub", "kokoro"], default="stub")
    parser.add_argument("--host", default="127.0.0.1", help="bind address; keep it loopback unless you mean it")
    parser.add_argument("--port", type=int, default=8880)
    parser.add_argument("--device", default=None, help="torch device for kokoro (cuda, cuda:1, cpu); default auto")
    parser.add_argument("--default-voice", default="af_heart")
    parser.add_argument("--token", default=None, help="optional bearer token required from clients")
    args = parser.parse_args()

    backend = KokoroBackend(args.device) if args.backend == "kokoro" else StubBackend()
    server = ThreadingHTTPServer((args.host, args.port), make_handler(backend, args.default_voice, args.token))
    print(f"aiva-server ({args.backend}) listening on http://{args.host}:{args.port}", file=sys.stderr)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
