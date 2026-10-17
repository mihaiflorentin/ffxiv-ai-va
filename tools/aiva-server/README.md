# aiva-server

A small optional reference server for the plugin's **Remote server** engine. It is not part of the
plugin zip. Use it, or any server that speaks the same endpoint, when you want synthesis on a GPU
the in-game engines cannot use (CUDA or ROCm on Linux, for example).

## Protocol

`POST {url}/v1/audio/speech`, JSON body:

| Field | Meaning |
|---|---|
| `model` | Free text, default `kokoro` (set with the `model` extra) |
| `input` | The line to speak |
| `voice` | Voice id, for example `af_heart` |
| `response_format` | Always `wav` |
| `speed` | Pace multiplier |
| `reference_wav` | Optional extension, base64 wav of a reference clip, for cloning servers. Other servers ignore it |

The reply must be a wav (16/24/32-bit PCM or 32-bit float, any sample rate; multi-channel is averaged to mono).
This reference server also answers `GET /health`.

## Run it

```bash
# No model, a tone per line. Checks that the plugin reaches the server.
python3 server.py --backend stub

# Kokoro on the GPU
python3 -m venv .venv && . .venv/bin/activate
pip install kokoro soundfile numpy
pip install torch                       # CUDA build from pytorch.org
# AMD: pip install torch --index-url https://download.pytorch.org/whl/rocm6.2
python3 server.py --backend kokoro      # picks cuda when torch sees a GPU
python3 server.py --backend kokoro --device cuda:1
python3 server.py --backend kokoro --device cpu
```

`kokoro` also needs the `espeak-ng` system package for some languages. ROCm builds of torch report
the GPU as `cuda`, so no flag change is needed there.

The server binds to `127.0.0.1:8880` by default. `--token` requires a bearer token; the plugin does
not send one yet, so leave it unset unless you front the server with something that adds it.

## Point the plugin at it

Set the engine to `remote` and, if the defaults do not fit, add these keys under
`Engine.PerEngine["remote"].Extra` in the plugin config:

| Key | Default | Meaning |
|---|---|---|
| `url` | `http://127.0.0.1:8880` | Server address |
| `allowNonLoopback` | unset | `true` lets the plugin send line text to a server that is not on this computer |
| `timeoutSeconds` | `30` | How long to wait for one line |
| `model` | `kokoro` | Sent as `model` |

Voices are the plugin's Kokoro voice ids (`af_heart`, `bf_emma`, ...), passed as `voice`.
