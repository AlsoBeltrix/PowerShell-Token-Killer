#!/usr/bin/env python3
"""Download one immutable candidate, preserving its API metadata for verification."""
import argparse
import json
import pathlib
import re
import subprocess
import sys

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("version")
parser.add_argument("source")
parser.add_argument("directory", type=pathlib.Path)
parser.add_argument("--rid", choices=("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64"))
args = parser.parse_args()
if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[A-Za-z0-9][A-Za-z0-9.-]*)?", args.version) or not re.fullmatch(r"[0-9a-f]{40}", args.source):
    parser.error("Expected a release version and full source SHA")
repository = "AlsoBeltrix/PowerShell-Token-Killer"
pages = json.loads(subprocess.check_output(["gh", "api", "--paginate", "--slurp", f"repos/{repository}/releases?per_page=100"]))
matches = [release for page in pages for release in page if release["tag_name"] == f"v{args.version}"]
if len(matches) != 1 or matches[0]["target_commitish"] != args.source:
    raise SystemExit("Expected exactly one release at the specified source SHA")
args.directory.mkdir(parents=True, exist_ok=False)
metadata_path = args.directory / "release.json"
metadata_path.write_text(json.dumps(matches[0], indent=2) + "\n", encoding="utf-8")
command = ["gh", "release", "download", f"v{args.version}", "--repo", repository, "--dir", str(args.directory)]
if args.rid:
    command += ["--pattern", f"*{args.rid}.*", "--pattern", "SHA256SUMS", "--pattern", "ptk-installer.zip"]
subprocess.run(command, check=True)
command = [sys.executable, "-B", str(pathlib.Path(__file__).with_name("verify-release-assets.py")),
           str(args.directory), str(metadata_path), args.version, args.source]
if args.rid:
    command += ["--rid", args.rid]
identities = subprocess.check_output(command)
(args.directory / "verified-identities.json").write_bytes(identities)
print(identities.decode())
