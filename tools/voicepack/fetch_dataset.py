#!/usr/bin/env python3
"""Download one config of the ylacombe/english_dialects dataset (CC-BY-SA-4.0) as parquet.

Usage: fetch_dataset.py <config> <data_dir> [--speakers]

  config      irish_male, scottish_female, northern_male, ... (there is no irish_female)
  data_dir    where <config>.parquet is written (multi-shard configs are merged into one file)
  --speakers  also print speaker ids with their utterance counts (to pick a speaker)

An already-present file is not downloaded again. Needs only the standard library plus
pyarrow (present in the KVoiceWalk venv) for merging shards and listing speakers.
"""
import json
import pathlib
import shutil
import sys
import urllib.request

API = "https://huggingface.co/api/datasets/ylacombe/english_dialects/parquet"


def shard_urls(config: str) -> list[str]:
    with urllib.request.urlopen(API, timeout=60) as response:
        listing = json.load(response)
    if config not in listing:
        raise SystemExit(f"unknown config '{config}'; available: {', '.join(sorted(listing))}")
    return listing[config]["train"]


def download(url: str, part: pathlib.Path, attempts: int = 6) -> None:
    """Download with a length check: a connection that ends early must not leave a short file."""
    for attempt in range(1, attempts + 1):
        print(f"downloading {url} (attempt {attempt})", flush=True)
        try:
            with urllib.request.urlopen(url, timeout=120) as response, open(part, "wb") as handle:
                expected = response.length
                shutil.copyfileobj(response, handle)
            if expected is None or part.stat().st_size == expected:
                return
            print(f"short read: {part.stat().st_size} of {expected} bytes", flush=True)
        except OSError as error:
            print(f"download error: {error}", flush=True)
    raise SystemExit(f"could not download {url}")


def fetch(config: str, data_dir: pathlib.Path) -> pathlib.Path:
    target = data_dir / f"{config}.parquet"
    if target.exists():
        return target
    data_dir.mkdir(parents=True, exist_ok=True)
    shards = []
    for index, url in enumerate(shard_urls(config)):
        part = data_dir / f"{config}.{index}.part.parquet"
        download(url, part)
        shards.append(part)
    if len(shards) == 1:
        shards[0].rename(target)
        return target

    import pyarrow.parquet as pq

    writer = None
    for part in shards:
        table = pq.read_table(part)
        writer = writer or pq.ParquetWriter(target, table.schema)
        writer.write_table(table)
    writer.close()
    for part in shards:
        part.unlink()
    return target


def main() -> None:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(args) != 2:
        raise SystemExit(__doc__)
    path = fetch(args[0], pathlib.Path(args[1]))
    print(f"dataset: {path}")
    if "--speakers" in sys.argv:
        import collections

        import pyarrow.parquet as pq

        counts = collections.Counter(pq.read_table(path, columns=["speaker_id"]).column(0).to_pylist())
        for speaker, count in sorted(counts.items()):
            print(f"speaker {speaker}: {count} utterances")


if __name__ == "__main__":
    main()
