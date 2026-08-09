#!/usr/bin/env python3
"""Point mod.json at one release asset and publish that file's sha256.

CI runs this after publishing a release, so the checksum tracks every build without anyone
remembering. The manual forms are for repairing drift:

    python3 tools/pin-download.py                            # newest release, via the API
    python3 tools/pin-download.py v0.1.252                   # a specific release
    python3 tools/pin-download.py --dist dist --tag v0.1.253 # hash the local zip (what CI runs)
    python3 tools/pin-download.py --check                    # report drift, write nothing

WHAT THE HASH IS FOR
--------------------
It pins the identity of a published artifact. PUNK Nexus REFUSES an install whose bytes do not
match, so once a release is out the manifest says "this exact file and no other", and a zip swapped
at the download url afterwards is rejected rather than installed.

WHY THE URL IS PINNED, NOT LEFT AS repo+assetPattern
----------------------------------------------------
`assetPattern` resolves to whatever the NEWEST release is. Pair that with a checksum and there is a
window, every single release, where the pointer has already moved to the new asset while the
manifest still carries the old hash -- and because a mismatch blocks, that window is one where
nobody can install this mod at all.

A pinned url has no such window. Until this script commits, the manifest names the PREVIOUS release
and the hash OF that release: internally consistent, just one build behind. The only visible effect
is that `version` may briefly read one release ahead of the download, which the client reports as an
unverified version label rather than refusing.

THE PACKAGED COPY
-----------------
build.ps1 copies mod.json into the zip, so the copy inside an artifact cannot contain that
artifact's own hash. It does not need to: the client verifies against the PUBLISHED manifest and
reads the packaged one only for the installed version. They are expected to differ in this field.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import urllib.request
from pathlib import Path

# CI runs this on windows-latest, where Python's stdout defaults to cp1252 and any non-ASCII
# character raises UnicodeEncodeError mid-print. Force UTF-8 so output can never fail the build.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

REPO = "Osanchez/PunkMultiverse"
ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "mod.json"
API = "https://api.github.com"


def asset_url(tag: str, name: str) -> str:
    return f"https://github.com/{REPO}/releases/download/{tag}/{name}"


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def from_release(tag: str | None) -> tuple[str, str, str] | None:
    """(tag, url, sha256) taken from the digest GitHub computed when the asset was uploaded."""
    req = urllib.request.Request(
        f"{API}/repos/{REPO}/releases/tags/{tag}" if tag else f"{API}/repos/{REPO}/releases/latest",
        headers={"Accept": "application/vnd.github+json", "User-Agent": "pin-download"},
    )
    with urllib.request.urlopen(req, timeout=60) as r:
        rel = json.load(r)

    for a in rel["assets"]:
        if a["name"].startswith("PunkMultiverse-v") and a["name"].endswith(".zip"):
            digest = a.get("digest") or ""
            if not digest.startswith("sha256:"):
                return None
            return rel["tag_name"], a["browser_download_url"], digest.split(":", 1)[1]
    return None


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("tag_positional", nargs="?", metavar="TAG")
    ap.add_argument("--tag")
    ap.add_argument("--dist", type=Path, help="hash the zip in this folder instead of calling the API")
    ap.add_argument("--check", action="store_true", help="report drift, write nothing")
    args = ap.parse_args()

    tag = args.tag or args.tag_positional
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    version = manifest.get("version")

    if args.dist:
        if not tag:
            print("--dist needs --tag (the release the zip will be published under)")
            return 2
        zip_path = args.dist / f"PunkMultiverse-v{version}.zip"
        if not zip_path.exists():
            print(f"no {zip_path.name} in {args.dist} - was the build packaged?")
            return 1
        url, sha = asset_url(tag, zip_path.name), sha256_of(zip_path)
    else:
        found = from_release(tag)
        if found is None:
            print(f"no PunkMultiverse zip with a sha256 digest on release {tag or '(latest)'}")
            return 1
        tag, url, sha = found

    print(f"release  {tag}")
    print(f"url      {url}")
    print(f"sha256   {sha}")

    current = manifest.get("download") or {}
    if current.get("url") == url and manifest.get("sha256") == sha:
        print("\nalready pinned; nothing to do")
        return 0

    if args.check:
        print("\nDRIFT: mod.json is not pinned to this release")
        return 1

    # Replaces repo+assetPattern outright. Leaving them beside a url would be two answers to the
    # same question, and the client would have to pick one.
    manifest["download"] = {"url": url}
    manifest["sha256"] = sha
    MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print("\npinned mod.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
