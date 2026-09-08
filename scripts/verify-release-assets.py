#!/usr/bin/env python3
"""Validate fresh release downloads against GitHub digests and SHA256SUMS."""
import argparse
import hashlib
import json
import pathlib
import re
import tarfile
import zipfile

RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64")


def verify(directory, metadata, version, source, rid=None):
    expected = {"ptk-installer.zip"}
    packages = {}
    for platform in RIDS:
        suffix = "zip" if platform.startswith("win-") else "tar.gz"
        for product, prefix in (("ptk", "ptk"), ("ptk-siem-receiver", "ptk-siem-receiver")):
            name = f"{prefix}-{version}-{platform}.{suffix}"
            expected.add(name)
            packages[name] = (product, platform)
    if metadata["tag_name"] != f"v{version}" or metadata["target_commitish"] != source:
        raise ValueError("Release tag/source does not match the requested candidate")
    assets = metadata["assets"]
    if len(assets) != 12 or {a["name"] for a in assets} != expected | {"SHA256SUMS"}:
        raise ValueError("Release must contain exactly the twelve expected assets")
    digests = {}
    for asset in assets:
        if asset["state"] != "uploaded" or not re.fullmatch(r"sha256:[0-9a-f]{64}", asset["digest"] or ""):
            raise ValueError(f"Missing uploaded GitHub digest: {asset['name']}")
        digests[asset["name"]] = asset["digest"][7:]
    sums_bytes = (directory / "SHA256SUMS").read_bytes()
    if hashlib.sha256(sums_bytes).hexdigest() != digests["SHA256SUMS"]:
        raise ValueError("SHA256SUMS differs from the GitHub asset digest")
    sums = {}
    for line in sums_bytes.decode("utf-8").splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  ([A-Za-z0-9._-]+)", line)
        if not match or match[2] in sums:
            raise ValueError("Malformed or duplicate SHA256SUMS entry")
        sums[match[2]] = match[1]
    if set(sums) != expected or any(sums[name] != digests[name] for name in expected):
        raise ValueError("Manifest inventory/hashes disagree with GitHub asset digests")
    selected = {name for name in expected if rid is None or name == "ptk-installer.zip" or packages[name][1] == rid}
    identities = []
    for name in sorted(selected):
        path = directory / name
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if digest != sums[name]:
            raise ValueError(f"Downloaded archive checksum mismatch: {name}")
        if path.suffix == ".zip":
            with zipfile.ZipFile(path) as archive:
                members = archive.namelist()
                provenance = [n for n in members if n.lstrip("./") == "BUILD-PROVENANCE.json"]
                data = archive.read(provenance[0]) if len(provenance) == 1 else None
        else:
            with tarfile.open(path, "r:gz") as archive:
                members = archive.getnames()
                provenance = [n for n in members if n.lstrip("./") == "BUILD-PROVENANCE.json"]
                data = archive.extractfile(provenance[0]).read() if len(provenance) == 1 else None
        for member in members:
            normalized = member.replace("\\", "/")
            if normalized.startswith("/") or ".." in normalized.split("/") or ":" in normalized:
                raise ValueError(f"Unsafe archive path: {name}: {member}")
        if name not in packages:
            continue
        if data is None:
            raise ValueError(f"Missing or duplicated package provenance: {name}")
        identity = json.loads(data)
        product, platform = packages[name]
        if (identity["schema_version"] != 1 or identity["product"] != product
                or identity["product_version"] != version or identity["source_commit"] != source
                or identity["source_dirty"] is not False or identity["target_rid"] != platform
                or not re.fullmatch(r"[0-9a-f]{32}", identity["build_identity"])):
            raise ValueError(f"Incorrect clean candidate identity: {name}")
        identities.append(identity)
    if len({i["build_identity"] for i in identities}) != len(identities):
        raise ValueError("Two package builds reused a build identity")
    return identities


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=pathlib.Path)
    parser.add_argument("metadata", type=pathlib.Path)
    parser.add_argument("version")
    parser.add_argument("source")
    parser.add_argument("--rid", choices=RIDS)
    options = parser.parse_args()
    print(json.dumps(verify(options.directory, json.loads(options.metadata.read_text()),
                            options.version, options.source, options.rid), indent=2))
