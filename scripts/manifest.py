#!/usr/bin/env python3
"""Builds the Jellyfin plugin repository manifest from the repo's GitHub releases.

Standard library only; run it as `python3 -I scripts/manifest.py --repo OWNER/NAME --out FILE`
(needs an authenticated `gh`). Every published `vX.Y.Z.W` release contributes one version, read
from its zip: md5 of the zip as published, targetAbi and changelog from the meta.json inside it.
The manifest is a pure function of the releases, so earlier versions are never lost.

Field names follow Jellyfin's PackageInfo and VersionInfo (MediaBrowser.Model/Updates).
"""
import argparse
import hashlib
import io
import json
import os
import re
import subprocess
import sys
import zipfile

IMAGE_URL = "https://overlabing.github.io/sillon/assets/icon.png"
_ZIP = re.compile(r"^Sillon_(\d+\.\d+\.\d+\.\d+)\.zip$")


def version_key(version):
    return tuple(int(part) for part in version.split("."))


def version_entry(release, asset, data):
    """One VersionInfo from a release, its zip asset and the zip's bytes."""
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        meta = json.loads(archive.read("meta.json"))
    if release["tag_name"] != "v" + meta["version"] or asset["name"] != "Sillon_%s.zip" % meta["version"]:
        raise ValueError("release %s holds %s with meta.json version %s"
                         % (release["tag_name"], asset["name"], meta["version"]))
    entry = {
        "version": meta["version"],
        "changelog": meta.get("changelog", ""),
        "targetAbi": meta["targetAbi"],
        "sourceUrl": asset["browser_download_url"],
        "checksum": hashlib.md5(data).hexdigest(),
        "timestamp": release["published_at"],
    }
    return entry, meta


def build(releases, fetch):
    """The manifest (a list with one PackageInfo) from GitHub release objects; fetch(asset) returns the zip bytes."""
    versions = []
    for release in releases:
        if release.get("draft") or release.get("prerelease"):
            continue
        assets = [a for a in release.get("assets", []) if _ZIP.match(a["name"])]
        if len(assets) != 1:
            raise ValueError("release %s has %d Sillon_*.zip assets" % (release["tag_name"], len(assets)))
        versions.append(version_entry(release, assets[0], fetch(assets[0])))
    if not versions:
        raise ValueError("no published release")
    versions.sort(key=lambda pair: version_key(pair[0]["version"]), reverse=True)
    newest = versions[0][1]
    guids = {meta["guid"] for _, meta in versions}
    if len(guids) != 1:
        raise ValueError("releases disagree on the plugin guid: %s" % sorted(guids))
    return [{
        "guid": newest["guid"],
        "name": newest["name"],
        "description": newest["description"],
        "overview": newest["overview"],
        "owner": newest["owner"],
        "category": newest["category"],
        "imageUrl": IMAGE_URL,
        "versions": [entry for entry, _ in versions],
    }]


def gh(*args):
    return subprocess.run(["gh", *args], check=True, capture_output=True).stdout


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--repo", required=True, help="OWNER/NAME")
    parser.add_argument("--out", required=True, help="manifest.json to write")
    args = parser.parse_args(argv)

    pages = json.loads(gh("api", "--paginate", "--slurp", "repos/%s/releases?per_page=100" % args.repo))
    releases = [release for page in pages for release in page]

    def fetch(asset):
        return gh("api", "-H", "Accept: application/octet-stream",
                  "repos/%s/releases/assets/%d" % (args.repo, asset["id"]))

    manifest = build(releases, fetch)
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w") as out:
        json.dump(manifest, out, indent=2)
        out.write("\n")
    print("OK: %s (%s)" % (args.out, ", ".join(v["version"] for v in manifest[0]["versions"])))
    return 0


if __name__ == "__main__":
    sys.exit(main())
