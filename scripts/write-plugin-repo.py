#!/usr/bin/env python3
import argparse
import json
import os
import time
import xml.etree.ElementTree as ET
from pathlib import Path


PROJECT_PATH = Path("src/AIVoiceActing/AIVoiceActing.csproj")
MANIFEST_PATH = Path("src/AIVoiceActing/AIVoiceActing.json")
PLUGIN_ZIP_NAME = "latest.zip"
DALAMUD_API_LEVEL = 15


def read_project_version() -> str:
    root = ET.parse(PROJECT_PATH).getroot()
    version = root.findtext("./PropertyGroup/Version")
    if not version:
        raise RuntimeError(f"Could not find <Version> in {PROJECT_PATH}")

    return version


def main() -> None:
    parser = argparse.ArgumentParser(description="Write a Dalamud custom plugin repository JSON.")
    parser.add_argument("--tag", required=True, help="Release tag, for example v1.0.0.")
    parser.add_argument("--output", required=True, type=Path, help="Output repo JSON path.")
    parser.add_argument(
        "--repository",
        default=os.environ.get("GITHUB_REPOSITORY", "mihaiflorentin88/ffxiv-ai-va"),
        help="GitHub repository in owner/name form.",
    )
    parser.add_argument(
        "--fixture",
        type=Path,
        default=None,
        help=(
            "Offline-test mode: read a canned release payload JSON ({tag, LastUpdate, "
            "repository?}) instead of validating the tag against the project version. "
            "Versions and release URLs derive from the fixture tag; --tag must match it "
            "when both are given. Field assembly is identical; LastUpdate and repository "
            "come from the fixture so the output is deterministic."
        ),
    )
    args = parser.parse_args()

    tag = args.tag
    fixture = None
    if args.fixture is not None:
        fixture = json.loads(args.fixture.read_text(encoding="utf-8-sig"))

        fixture_tag = fixture.get("tag")
        if not isinstance(fixture_tag, str) or not fixture_tag:
            raise RuntimeError(
                f"Fixture {args.fixture} is missing or has invalid required field 'tag' (non-empty string)."
            )

        last_update = fixture.get("LastUpdate")
        if isinstance(last_update, bool) or not isinstance(last_update, int):
            raise RuntimeError(
                f"Fixture {args.fixture} is missing or has invalid required field 'LastUpdate' (integer)."
            )

        fixture_repository = fixture.get("repository")
        if fixture_repository is not None and not isinstance(fixture_repository, str):
            raise RuntimeError(
                f"Fixture {args.fixture} has invalid optional field 'repository' (string expected)."
            )

        tag = fixture_tag
        tag_version = tag[1:] if tag.startswith("v") else tag
        args_tag_version = args.tag[1:] if args.tag.startswith("v") else args.tag
        if args_tag_version != tag_version:
            raise RuntimeError(f"--tag {args.tag} does not match fixture tag {tag}")

        version = tag_version
        last_update = int(last_update)
    else:
        version = read_project_version()
        tag_version = tag[1:] if tag.startswith("v") else tag
        if tag_version != version:
            raise RuntimeError(f"Tag {tag} does not match project version {version}")
        last_update = int(time.time())


    repository = (fixture or {}).get("repository", args.repository)
    manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8-sig"))
    repo_url = f"https://github.com/{repository}"
    download_url = f"{repo_url}/releases/download/{tag}/{PLUGIN_ZIP_NAME}"

    entry = {
        "Author": manifest["Author"],
        "Name": manifest["Name"],
        "Punchline": manifest["Punchline"],
        "Description": manifest["Description"],
        "InternalName": manifest["InternalName"],
        "AssemblyVersion": version,
        "TestingAssemblyVersion": version,
        "RepoUrl": repo_url,
        "ApplicableVersion": manifest.get("ApplicableVersion", "any"),
        "DalamudApiLevel": DALAMUD_API_LEVEL,
        "TestingDalamudApiLevel": DALAMUD_API_LEVEL,
        "IsHide": False,
        "IsTestingExclusive": False,
        "DownloadCount": 0,
        "LastUpdate": last_update,
        "DownloadLinkInstall": download_url,
        "DownloadLinkTesting": download_url,
        "DownloadLinkUpdate": download_url,
        "LoadPriority": 0,
        "LoadRequiredState": 0,
        "LoadSync": False,
        "CanUnloadAsync": False,
        "SupportsProfiles": True,
        "ImageUrls": None,
        "IconUrl": f"https://raw.githubusercontent.com/{repository}/main/src/AIVoiceActing/icon.png",
        "Tags": manifest.get("Tags", []),
    }

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps([entry], indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
