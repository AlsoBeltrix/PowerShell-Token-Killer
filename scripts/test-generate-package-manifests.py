#!/usr/bin/env python3
import importlib.util
import json
import pathlib
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("package_manifests", pathlib.Path(__file__).with_name("generate-package-manifests.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ManifestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.output = pathlib.Path(self.temp.name)
        self.metadata = {"tag_name": "v0.3.0-rc.3", "draft": False, "assets": []}
        for number, rid in enumerate(("win-x64", "win-arm64", "osx-arm64", "linux-x64", "linux-arm64"), 1):
            suffix = "zip" if rid.startswith("win-") else "tar.gz"
            name = f"ptk-0.3.0-rc.3-{rid}.{suffix}"
            self.metadata["assets"].append({"name": name, "digest": "sha256:" + str(number) * 64,
                "browser_download_url": module.REPOSITORY + "/releases/download/v0.3.0-rc.3/" + name})

    def tearDown(self):
        self.temp.cleanup()

    def test_unpublished_release_cannot_become_a_package(self):
        self.metadata["draft"] = True
        with self.assertRaisesRegex(ValueError, "published release"):
            module.generate(self.metadata, self.output)
        self.assertEqual([], list(self.output.iterdir()))

    def test_assets_cannot_redirect_to_another_publisher(self):
        self.metadata["assets"][2]["browser_download_url"] = "https://example.invalid/ptk.tar.gz"
        with self.assertRaisesRegex(ValueError, "Unexpected asset"):
            module.generate(self.metadata, self.output)

    def test_architecture_hashes_and_package_owned_stable_paths(self):
        module.generate(self.metadata, self.output)
        scoop = json.loads((self.output / "scoop/bucket/ptk.json").read_text())
        self.assertEqual("1" * 64, scoop["architecture"]["64bit"]["hash"])
        self.assertEqual("2" * 64, scoop["architecture"]["arm64"]["hash"])
        self.assertEqual("main/rtk", scoop["depends"])
        self.assertIn('"current"', scoop["post_install"])
        self.assertNotIn("ptk init", scoop["post_install"])
        formula = (self.output / "homebrew/Formula/ptk.rb").read_text()
        self.assertIn('sha256 "' + "3" * 64 + '"', formula)
        self.assertIn('PACKAGE-HOME', formula)
        self.assertIn('#{opt_libexec}', formula)
        installer = next((self.output / "winget").rglob("*.installer.yaml")).read_text()
        self.assertIn("InstallerSha256: " + "1" * 64, installer)
        self.assertIn("InstallerSha256: " + "2" * 64, installer)
        self.assertIn("RelativeFilePath: bin\\ptk.exe", installer)
        pkgbuild = (self.output / "aur/PKGBUILD").read_text()
        self.assertIn("sha256sums_x86_64=('" + "4" * 64 + "')", pkgbuild)
        self.assertIn("sha256sums_aarch64=('" + "5" * 64 + "')", pkgbuild)
        self.assertIn("options=('!strip' '!debug')", pkgbuild)


if __name__ == "__main__":
    unittest.main()
