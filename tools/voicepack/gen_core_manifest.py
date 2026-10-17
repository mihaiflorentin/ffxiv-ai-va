#!/usr/bin/env python3
"""Regenerate src/AIVoiceActing/Domain/voices-core.json from the KokoroSharp voice bank.

Usage: gen_core_manifest.py [VOICES_DIR] [OUT_JSON]

Accent and sex come from the Kokoro id prefix (the same rules as Domain/VoiceCatalog.cs):
first letter = language bank, second = sex. Ids are never renamed.
"""
import json
import pathlib
import sys

LANG = {
    "a": ("en-US", "American English"),
    "b": ("en-GB", "British English"),
    "e": ("es-ES", "Spanish"),
    "f": ("fr-FR", "French"),
    "i": ("it-IT", "Italian"),
    "p": ("pt-BR", "Brazilian Portuguese"),
    "h": ("hi-IN", "Hindi"),
    "j": ("ja-JP", "Japanese"),
    "z": ("zh-CN", "Mandarin Chinese"),
}
SEX = {"f": "female", "m": "male"}
EXCLUDE = {"am_irishtest"}  # local experiment tensor, not a stock voice


def main() -> None:
    root = pathlib.Path(__file__).resolve().parents[2]
    default_dir = pathlib.Path.home() / ".nuget/packages/kokorosharp/0.8.4/content/voices"
    voices_dir = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else default_dir
    out = pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else root / "src/AIVoiceActing/Domain/voices-core.json"

    voices = []
    for path in sorted(voices_dir.glob("*.npy")):
        vid = path.stem
        if vid in EXCLUDE or len(vid) < 4 or vid[2] != "_" or vid[0] not in LANG or vid[1] not in SEX:
            continue
        accent, label = LANG[vid[0]]
        sex = SEX[vid[1]]
        voices.append({
            "id": vid,
            "name": vid[3:].replace("_", " ").title(),
            "accent": accent,
            "sex": sex,
            "description": f"{label} {sex} voice.",
            "tags": [],
        })

    doc = {
        "schema": 1,
        "packId": "aiva-kokoro-stock",
        "packName": "Kokoro stock voices",
        "version": "1.0.0",
        "license": "Apache-2.0",
        "attribution": "Voice styles from Kokoro-82M by hexgrad (Apache-2.0).",
        "voices": voices,
    }
    out.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {len(voices)} voices to {out}")


if __name__ == "__main__":
    main()
