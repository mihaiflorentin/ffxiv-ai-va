#!/usr/bin/env python3
"""Regenerate one voice (or all) of a voice pack from its generator spec.

  build_voice.py isles.ie_m_01                  # every stage for one voice
  build_voice.py isles.sc_m_01 --stages walk,tensor,clip,manifest,preview --steps 1500
  build_voice.py --all --stages clip,manifest   # cheap stages for the whole pack

Stages, in order (default: all):
  fetch     download the dataset config (CC-BY-SA-4.0 parquet) into <work>/data
  target    cut a ~25 s target clip + transcript for the speaker into <work>/targets
  walk      run KVoiceWalk (random walk over Kokoro style tensors) toward the target;
            a later run continues from the best tensor found so far
  tensor    best .pt of every walk -> <pack>/style/<name>.npy, scores -> <spec>.scores.json
  clip      reference clip (<=10 s, whole sentences) + transcript for cloning engines
  manifest  rewrite <pack>/voicepack.json from the spec for every voice that has a tensor
  preview   render the 3 s preview through SmokeSynth (--audition) into <pack>/preview

Needs a KVoiceWalk checkout with a venv under <work>/kvoicewalk (see README.md). The pack
folder is taken from the spec ("packDir", repo-relative) unless --pack is given.
"""
import argparse
import fcntl
import json
import os
import pathlib
import re
import subprocess
import sys
import time
import wave

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parents[1]
STAGES = ["fetch", "target", "walk", "tensor", "clip", "manifest", "preview"]
MAX_CLIP_SECONDS = 10.0


def short_name(voice_id: str, pack_prefix: str) -> str:
    return voice_id[len(pack_prefix) + 1:] if voice_id.startswith(pack_prefix + ".") else voice_id


def default_work() -> str:
    """AIVA_VOICEWALK_WORK, else models/kvoicewalk-work in this checkout or the checkout a
    git worktree hangs off (models/ is never committed, so a worktree has none of its own)."""
    if os.environ.get("AIVA_VOICEWALK_WORK"):
        return os.environ["AIVA_VOICEWALK_WORK"]
    for base in [REPO, *REPO.parents]:
        if (base / "models" / "kvoicewalk-work").is_dir():
            return str(base / "models" / "kvoicewalk-work")
    return str(REPO / "models" / "kvoicewalk-work")


class Context:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.spec_path = pathlib.Path(args.spec).resolve()
        self.spec = json.loads(self.spec_path.read_text(encoding="utf-8"))
        self.work = pathlib.Path(args.work).resolve()
        self.pack = pathlib.Path(args.pack).resolve() if args.pack else REPO / self.spec["pack"]["packDir"]
        self.py = str(self.work / "kvoicewalk" / ".venv" / "bin" / "python")
        self.defaults = self.spec.get("defaults", {})
        self.prefix = self.spec["pack"]["id"].split("-")[-1]  # "aiva-isles" -> "isles"

    def voice(self, voice_id: str) -> dict:
        for voice in self.spec["voices"]:
            if voice["id"] == voice_id or short_name(voice["id"], self.prefix) == voice_id:
                return voice
        raise SystemExit(f"voice '{voice_id}' is not in {self.spec_path.name}")

    def name(self, voice: dict) -> str:
        return short_name(voice["id"], self.prefix)

    def run(self, cmd: list[str], **kwargs) -> None:
        print("+", " ".join(cmd), flush=True)
        subprocess.run(cmd, check=True, **kwargs)

    # --- stages ---------------------------------------------------------------------------

    def fetch(self, voice: dict) -> None:
        self.run([self.py, "-I", str(HERE / "fetch_dataset.py"), voice["dataset"], str(self.work / "data")])

    def target(self, voice: dict) -> None:
        wav = self.work / "targets" / f"{self.name(voice)}.wav"
        if wav.exists() and wav.with_suffix(".json").exists() and not self.args.force:
            print(f"target exists: {wav}")
            return
        parquet = self.work / "data" / f"{voice['dataset']}.parquet"
        goal = voice.get("targetSeconds", self.defaults.get("targetSeconds", 24))
        self.run([self.py, "-I", str(HERE / "make_target.py"), str(parquet), str(voice["speaker"]), str(wav), str(goal)])

    def runs(self, name: str) -> list[pathlib.Path]:
        return sorted((self.work / "kvoicewalk" / "out").glob(f"{name}_{name}_*"))

    def best_tensor(self, name: str) -> tuple[pathlib.Path, int, float, float] | None:
        """Highest-scoring .pt over every run of this voice: (path, step, score, similarity)."""
        pattern = re.compile(rf"^{re.escape(name)}_(\d+)_([\d.]+)_([\d.]+)_{re.escape(name)}\.pt$")
        best = None
        for run in self.runs(name):
            for path in run.glob("*.pt"):
                match = pattern.match(path.name)
                if match:
                    step, score, sim = int(match[1]), float(match[2]), float(match[3])
                    if best is None or (score, step) > (best[2], best[1]):
                        best = (path, step, score, sim)
        return best

    def seed_folder(self, voice: dict) -> pathlib.Path | None:
        """KVoiceWalk scores every voice in its voices/ folder before it starts walking (about
        30 s each on a busy CPU). The spec lists a short, sex-matched seed set instead."""
        names = self.defaults.get("seedVoices", {}).get(voice["sex"])
        if not names:
            return None
        folder = self.work / "seed-voices" / voice["sex"]
        folder.mkdir(parents=True, exist_ok=True)
        for name in names:
            link = folder / f"{name}.pt"
            if not link.exists():
                link.symlink_to(self.work / "kvoicewalk" / "voices" / f"{name}.pt")
        return folder

    def walk(self, voice: dict) -> None:
        name = self.name(voice)
        steps = self.args.steps or voice.get("steps") or self.defaults.get("steps", 1000)
        start = self.args.start_from
        if start is None and not self.args.fresh and (previous := self.best_tensor(name)):
            start = str(previous[0])
            print(f"continuing from {start} (step {previous[1]}, similarity {previous[3]})")
        targets = self.work / "targets"
        cmd = [
            self.args.walk_python or self.py, "main.py",
            "--target_audio", str(targets / f"{name}.wav"),
            "--target_text", str(targets / f"{name}.txt"),
            "--device", self.args.device,
            "--step_limit", str(steps),
            "--output_name", name,
        ]
        if start:
            cmd += ["--starting_voice", start]
        if (seed := self.seed_folder(voice)) is not None:
            cmd += ["--voice_folder", str(seed)]
        env = dict(os.environ, HF_HOME=str(self.work / "hf"), PYTHONUNBUFFERED="1")
        threads = str(self.args.threads)
        env.update(OMP_NUM_THREADS=threads, MKL_NUM_THREADS=threads, OPENBLAS_NUM_THREADS=threads)
        pause = self.work / "PAUSE"
        while pause.exists() and self.args.device == "cpu":  # touch <work>/PAUSE to hold back new walks (e.g. while the machine is in use)
            print(f"{pause} exists; waiting", flush=True)
            time.sleep(60)
        log_dir = self.work / "logs"
        log_dir.mkdir(exist_ok=True)
        print("+", " ".join(cmd), f"(log: {log_dir / (name + '.log')})", flush=True)
        with open(log_dir / f"{name}.log", "ab") as log:
            subprocess.run(cmd, check=True, cwd=self.work / "kvoicewalk", env=env, stdout=log, stderr=subprocess.STDOUT)

    def given_tensor(self) -> tuple[pathlib.Path, int, float, float]:
        """--tensor-file: KVoiceWalk names its output <name>_<step>_<score>_<sim>_<target>.pt; read the numbers if present."""
        path = pathlib.Path(self.args.tensor_file)
        match = re.search(r"_(\d+)_(\d+\.\d+)_(\d+\.\d+)_", path.name)
        return (path, int(match[1]), float(match[2]), float(match[3])) if match else (path, 0, 0.0, 0.0)

    def tensor(self, voice: dict) -> None:
        name = self.name(voice)
        best = self.given_tensor() if self.args.tensor_file else self.best_tensor(name)
        if not best:
            raise SystemExit(f"no walk output for {name}; run the walk stage first")
        out = self.pack / "style" / f"{name}.npy"
        out.parent.mkdir(parents=True, exist_ok=True)
        self.run([self.py, "-I", str(HERE / "pt_to_npy.py"), str(best[0]), str(out)])
        record = {
            "step": best[1], "score": best[2], "similarity": best[3],
            "dataset": voice["dataset"], "speaker": voice["speaker"],
        }
        scores = self.spec_path.with_name(self.spec_path.name.removesuffix(".voices.json").removesuffix(".json") + ".scores.json")
        with open(self.spec_path, "rb") as lock:  # the spec file doubles as the lock
            fcntl.flock(lock, fcntl.LOCK_EX)
            data = json.loads(scores.read_text(encoding="utf-8")) if scores.exists() else {}
            data[voice["id"]] = record
            scores.write_text(json.dumps(dict(sorted(data.items())), indent=2) + "\n", encoding="utf-8")
        print(f"{voice['id']}: step {best[1]} similarity {best[3]}")

    def clip(self, voice: dict) -> None:
        name = self.name(voice)
        clips = self.pack / "clips"
        clips.mkdir(parents=True, exist_ok=True)
        parquet = self.work / "data" / f"{voice['dataset']}.parquet"
        self.run([
            self.py, "-I", str(HERE / "make_clip.py"), str(parquet), str(voice["speaker"]),
            str(clips / f"{name}.wav"), str(clips / f"{name}.txt"), str(MAX_CLIP_SECONDS - 0.5),
        ])

    def manifest(self, _voice: dict | None = None) -> None:
        pack = self.spec["pack"]
        voices = []
        for voice in self.spec["voices"]:
            name = self.name(voice)
            if not (self.pack / "style" / f"{name}.npy").exists():
                continue
            realizations = [{
                "kind": "StyleTensor",
                "file": f"style/{name}.npy",
                "phonemizer": voice.get("phonemizer", self.defaults.get("phonemizer", "en-us")),
            }]
            if (self.pack / "clips" / f"{name}.wav").exists():
                realizations.append({
                    "kind": "ReferenceClip", "file": f"clips/{name}.wav", "transcript": f"clips/{name}.txt",
                })
            entry = {
                "id": voice["id"], "name": voice["name"], "accent": voice["accent"], "sex": voice["sex"],
                "age": voice.get("age", self.defaults.get("age", "adult")), "tags": voice.get("tags", []),
                "description": voice.get("description", ""), "quality": "experimental",
            }
            entry["previewFile"] = f"preview/{name}.wav"  # rendered by the preview stage (SmokeSynth --audition)
            entry["realizations"] = realizations
            voices.append(entry)
        document = {
            "schema": 1, "id": pack["id"], "version": pack["version"], "name": pack["name"],
            "license": pack["license"], "voices": voices,
        }
        self.pack.mkdir(parents=True, exist_ok=True)
        (self.pack / "voicepack.json").write_text(json.dumps(document, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"wrote {self.pack / 'voicepack.json'} ({len(voices)} voices)")

    def preview(self, voice: dict) -> None:
        # The manifest must already list the voice (and its preview file) so --audition finds it.
        self.manifest()
        cmd = [
            "dotnet", "run", "--project", str(REPO / "tools" / "SmokeSynth"), "-c", "Release",
            "-p:UseAppHost=false", "-p:AllowMissingPrunePackageData=true", "--",
            "--audition", str(self.pack), "--only", voice["id"], "--out", str(self.pack / "preview"),
            "--models", self.args.models,
        ]
        self.run(cmd)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("voice", nargs="?", help="voice id (isles.ie_m_01) or short name (ie_m_01)")
    parser.add_argument("--all", action="store_true", help="every voice in the spec")
    parser.add_argument("--spec", default=str(HERE / "isles.voices.json"))
    parser.add_argument("--pack", help="pack folder (default: the spec's packDir)")
    parser.add_argument("--work", default=default_work(),
                        help="work dir holding kvoicewalk/, data/, targets/ (default: $AIVA_VOICEWALK_WORK or models/kvoicewalk-work)")
    parser.add_argument("--stages", default=",".join(STAGES))
    parser.add_argument("--steps", type=int, help="walk steps (default: the spec, else 1000)")
    parser.add_argument("--device", default="cpu", choices=["cpu", "cuda"], help="KVoiceWalk device (cuda also means ROCm)")
    parser.add_argument("--walk-python", help="python for the walk stage only, e.g. a ROCm/CUDA torch venv (see README)")
    parser.add_argument("--threads", type=int, default=3, help="CPU threads for the walk")
    parser.add_argument("--start-from", help="continue the walk from this .pt")
    parser.add_argument("--fresh", action="store_true", help="ignore earlier walk output instead of continuing")
    parser.add_argument("--tensor-file", help="use this .pt instead of the best walk output")
    parser.add_argument("--force", action="store_true", help="rebuild an existing target clip")
    parser.add_argument("--models", default=str(pathlib.Path.home() / ".cache" / "aiva-kokoro"))
    args = parser.parse_args()
    if bool(args.voice) == args.all:
        parser.error("give a voice id or --all")
    stages = args.stages.split(",")
    for stage in stages:
        if stage not in STAGES:
            parser.error(f"unknown stage '{stage}'; choose from {', '.join(STAGES)}")

    ctx = Context(args)
    voices = ctx.spec["voices"] if args.all else [ctx.voice(args.voice)]
    for voice in voices:
        for stage in STAGES:
            if stage in stages:
                print(f"== {voice['id']}: {stage}", flush=True)
                getattr(ctx, stage)(voice)


if __name__ == "__main__":
    sys.exit(main())
