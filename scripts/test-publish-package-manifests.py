#!/usr/bin/env python3
import importlib.util
import json
import os
import pathlib
import subprocess
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location("package_publisher", pathlib.Path(__file__).with_name("publish-package-manifests.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PublicationTests(unittest.TestCase):
    def test_publication_preserves_other_packages_and_identical_retry_is_a_noop(self):
        with tempfile.TemporaryDirectory() as temporary, mock.patch.dict(os.environ, {
            "GIT_AUTHOR_NAME": "PTK publication fixture", "GIT_AUTHOR_EMAIL": "fixture@example.invalid",
            "GIT_COMMITTER_NAME": "PTK publication fixture", "GIT_COMMITTER_EMAIL": "fixture@example.invalid",
        }):
            root = pathlib.Path(temporary)
            seed = root / "seed"
            remote = root / "remote.git"

            def git(*args, cwd=None):
                return subprocess.check_output(["git", *args], cwd=cwd, stderr=subprocess.STDOUT)

            git("init", "--initial-branch=main", str(seed))
            (seed / "Formula").mkdir()
            (seed / "Formula/foreign.rb").write_text("preserve other package\n")
            (seed / "README.md").write_text("preserve owner documentation\n")
            git("add", ".", cwd=seed)
            git("commit", "-m", "fixture", cwd=seed)
            git("clone", "--bare", str(seed), str(remote))
            manifests = root / "manifests"
            (manifests / "homebrew/Formula").mkdir(parents=True)
            (manifests / "homebrew/Formula/ptk.rb").write_text('version "0.3.0-rc.3"\n')

            def local_run(*args, cwd=None):
                command = list(args)
                if command[:2] == ["git", "clone"]:
                    command[-2] = str(remote)
                subprocess.run(command, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)

            proof = "https://github.com/AlsoBeltrix/PowerShell-Token-Killer/actions/runs/123"
            with mock.patch.object(module, "run", side_effect=local_run):
                first = root / "first"
                first.mkdir()
                module.publish("homebrew", manifests, "0.3.0-rc.3", proof, first)
                commit = git("rev-parse", "main", cwd=remote)
                self.assertEqual(b"preserve other package\n", git("show", "main:Formula/foreign.rb", cwd=remote))
                self.assertEqual(b"preserve owner documentation\n", git("show", "main:README.md", cwd=remote))
                self.assertEqual(b'version "0.3.0-rc.3"\n', git("show", "main:Formula/ptk.rb", cwd=remote))
                second = root / "second"
                second.mkdir()
                module.publish("homebrew", manifests, "0.3.0-rc.3", proof, second)
                self.assertEqual(commit, git("rev-parse", "main", cwd=remote))


    def test_an_existing_winget_request_is_found_by_its_bare_branch_and_owner(self):
        # gh pr list --head matches the bare branch name only; an
        # owner-prefixed value matches nothing (observed 2026-10-27 against
        # microsoft/winget-pkgs#463601), so the owner is filtered separately.
        requests = [
            {"url": "https://github.com/microsoft/winget-pkgs/pull/1", "state": "OPEN",
             "headRepositoryOwner": {"login": "someone-else"}},
            {"url": "https://github.com/microsoft/winget-pkgs/pull/2", "state": "OPEN",
             "headRepositoryOwner": {"login": "roethlar"}},
        ]

        def gh(command):
            head = command[command.index("--head") + 1]
            return json.dumps(requests if head == "ptk-0.3.0-rc.6" else [])

        proof = "https://github.com/AlsoBeltrix/PowerShell-Token-Killer/actions/runs/123"
        with tempfile.TemporaryDirectory() as temporary, \
                mock.patch.object(module.subprocess, "check_output", side_effect=gh), \
                mock.patch.object(module, "run") as run, \
                mock.patch("builtins.print") as printed:
            module.publish("winget", pathlib.Path(temporary), "0.3.0-rc.6", proof, pathlib.Path(temporary))
        run.assert_not_called()
        self.assertIn("/pull/2", printed.call_args.args[0])
        self.assertNotIn("/pull/1", printed.call_args.args[0])

    def test_the_workflow_looks_up_winget_requests_the_same_way(self):
        workflow = pathlib.Path(__file__).parents[1] / ".github/workflows/publish-packages.yml"
        text = workflow.read_text()
        self.assertIn('--head "ptk-$PTK_PACKAGE_VERSION"', text)
        self.assertIn('select(.headRepositoryOwner.login=="roethlar")', text)
        self.assertNotIn("gh pr list -R microsoft/winget-pkgs --head \"roethlar:", text)


if __name__ == "__main__":
    unittest.main()
