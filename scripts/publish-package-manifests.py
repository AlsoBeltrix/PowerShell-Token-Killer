#!/usr/bin/env python3
"""Publish already-validated manifests; winget publication creates an upstream PR."""
import argparse
import json
import os
import pathlib
import re
import shutil
import subprocess
import tempfile


def run(*args, cwd=None):
    subprocess.run(args, cwd=cwd, check=True)


def publish(channel, manifests, version, proof_url, work):
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[A-Za-z0-9][A-Za-z0-9.-]*)?", version):
        raise ValueError("Invalid release version")
    if not re.fullmatch(r"https://github.com/AlsoBeltrix/PowerShell-Token-Killer/actions/runs/\d+", proof_url):
        raise ValueError("A canonical package-validation run URL is required")
    repository = {"homebrew": "roethlar/homebrew-tap", "scoop": "roethlar/scoop-bucket",
                  "winget": "roethlar/winget-pkgs", "aur": None}[channel]
    checkout = work / channel
    if channel == "winget":
        branch = f"ptk-{version}"
        existing = json.loads(subprocess.check_output(["gh", "pr", "list", "--repo", "microsoft/winget-pkgs",
            "--head", "roethlar:" + branch, "--state", "all", "--json", "url,state"]))
        if existing:
            print(json.dumps(existing))
            return
        run("git", "clone", "--filter=blob:none", "--no-checkout", "--depth=1", f"https://github.com/{repository}.git", str(checkout))
        run("git", "fetch", "--depth=1", "https://github.com/microsoft/winget-pkgs.git", "master", cwd=checkout)
        relative = f"manifests/r/roethlar/PowerShellTokenKiller/{version}"
        run("git", "sparse-checkout", "set", "manifests/r/roethlar/PowerShellTokenKiller", cwd=checkout)
        run("git", "switch", "--create", branch, "FETCH_HEAD", cwd=checkout)
        if (checkout / relative).exists():
            print(f"winget already contains {relative}")
            return
        shutil.copytree(manifests / "winget" / relative, checkout / relative)
        staged = [relative]
    elif channel == "aur":
        run("git", "clone", "ssh://aur@aur.archlinux.org/ptk-bin.git", str(checkout))
        staged = ["PKGBUILD", ".SRCINFO"]
        for filename in staged:
            shutil.copyfile(manifests / "aur" / filename, checkout / filename)
    else:
        remote = f"git@github.com:{repository}.git" if os.environ.get("PTK_USE_DEPLOY_KEY") else f"https://github.com/{repository}.git"
        run("git", "clone", "--depth=1", remote, str(checkout))
        relative = "Formula/ptk.rb" if channel == "homebrew" else "bucket/ptk.json"
        destination = checkout / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(manifests / channel / relative, destination)
        staged = [relative]
    run("git", "add", "--", *staged, cwd=checkout)
    run("git", "diff", "--cached", "--check", cwd=checkout)
    changed = subprocess.check_output(["git", "diff", "--cached", "--name-only"], cwd=checkout).strip()
    if not changed:
        print(f"{channel}: manifests already match {version}")
        return
    run("git", "commit", "-m", f"ptk: publish {version}", cwd=checkout)
    if channel == "winget":
        run("git", "push", "--set-upstream", "origin", branch, cwd=checkout)
        body = work / "winget-pr.md"
        body.write_text(f"Adds PowerShell Token Killer {version} for x64 and ARM64 Windows. "
            "The signed self-contained ZIP installs the native `ptk` command and declares RTK as a dependency. "
            "Harness registration is explicit via `ptk init --agent codex`.\n\n"
            f"Validation: [native package installation and removal]({proof_url}).\n", encoding="utf-8")
        run("gh", "pr", "create", "--repo", "microsoft/winget-pkgs", "--head", "roethlar:" + branch,
            "--title", f"New package: roethlar.PowerShellTokenKiller version {version}", "--body-file", str(body), cwd=checkout)
    else:
        run("git", "push", "origin", "HEAD", cwd=checkout)
        print(f"{channel}: published {version}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("channel", choices=("homebrew", "scoop", "winget", "aur"))
    parser.add_argument("manifests", type=pathlib.Path)
    parser.add_argument("version")
    parser.add_argument("proof_url")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="ptk-package-publish-") as directory:
        publish(args.channel, args.manifests.resolve(), args.version, args.proof_url, pathlib.Path(directory))
