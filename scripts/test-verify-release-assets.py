#!/usr/bin/env python3
import copy
import hashlib
import importlib.util
import io
import json
import pathlib
import tarfile
import tempfile
import unittest
import uuid
import zipfile

spec = importlib.util.spec_from_file_location("release_assets", pathlib.Path(__file__).with_name("verify-release-assets.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DownloadVerificationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temporary.name)
        self.version = "0.3.0-rc.3"
        self.source = "e" * 40
        self.metadata = {"tag_name": "v" + self.version, "target_commitish": self.source, "assets": []}
        sums = []
        for rid in module.RIDS:
            for product in ("ptk", "ptk-siem-receiver"):
                data = json.dumps({"schema_version": 1, "product": product,
                    "product_version": self.version, "source_commit": self.source,
                    "source_dirty": False, "target_rid": rid, "build_identity": uuid.uuid4().hex}).encode()
                suffix = "zip" if rid.startswith("win-") else "tar.gz"
                path = self.root / f"{product}-{self.version}-{rid}.{suffix}"
                if suffix == "zip":
                    with zipfile.ZipFile(path, "w") as archive:
                        archive.writestr("BUILD-PROVENANCE.json", data)
                else:
                    with tarfile.open(path, "w:gz") as archive:
                        info = tarfile.TarInfo("./BUILD-PROVENANCE.json")
                        info.size = len(data)
                        archive.addfile(info, io.BytesIO(data))
        with zipfile.ZipFile(self.root / "ptk-installer.zip", "w") as archive:
            archive.writestr("install.ps1", "installer")
        for path in sorted(self.root.iterdir()):
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            sums.append(f"{digest}  {path.name}\n")
            self.metadata["assets"].append({"name": path.name, "state": "uploaded", "digest": "sha256:" + digest})
        self.write_manifest("".join(sums))

    def tearDown(self):
        self.temporary.cleanup()

    def write_manifest(self, content):
        data = content.encode()
        (self.root / "SHA256SUMS").write_bytes(data)
        self.metadata["assets"] = [a for a in self.metadata["assets"] if a["name"] != "SHA256SUMS"]
        self.metadata["assets"].append({"name": "SHA256SUMS", "state": "uploaded", "digest": "sha256:" + hashlib.sha256(data).hexdigest()})

    def verify(self, **kwargs):
        return module.verify(self.root, self.metadata, self.version, self.source, **kwargs)

    def test_all_ten_identities_and_native_subset(self):
        self.assertEqual(10, len(self.verify()))
        self.assertEqual(2, len(self.verify(rid="win-arm64")))

    def test_changed_download_is_refused(self):
        with (self.root / "ptk-installer.zip").open("ab") as stream:
            stream.write(b"changed")
        with self.assertRaisesRegex(ValueError, "checksum mismatch"):
            self.verify()

    def test_wrong_candidate_source_is_refused(self):
        self.metadata["target_commitish"] = "a" * 40
        with self.assertRaisesRegex(ValueError, "tag/source"):
            self.verify()

    def test_duplicate_manifest_and_extra_asset_are_refused(self):
        manifest = (self.root / "SHA256SUMS").read_text()
        self.write_manifest(manifest + manifest.splitlines()[0] + "\n")
        with self.assertRaisesRegex(ValueError, "duplicate"):
            self.verify()
        self.write_manifest(manifest)
        self.metadata["assets"].append(copy.deepcopy(self.metadata["assets"][0]))
        with self.assertRaisesRegex(ValueError, "twelve"):
            self.verify()

    def test_github_digest_and_manifest_must_agree_even_for_other_rids(self):
        self.metadata["assets"][0]["digest"] = "sha256:" + "0" * 64
        with self.assertRaisesRegex(ValueError, "disagree"):
            self.verify(rid="win-arm64")


if __name__ == "__main__":
    unittest.main()
