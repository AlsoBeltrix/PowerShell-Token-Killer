# rr-3: release workflow did not exercise packaged activation

**Severity:** HIGH
**Status:** FIXED in the packaged-install-gate slice after `6003273`
**Scope:** `.github/workflows/release.yml`, `server/test-staged-install.ps1`

## Finding

The five RID jobs built and tested each staged package, but none invoked the
existing packaged-install transaction proof. A release could therefore pass
its workflow without proving that the transaction module shipped inside that
exact package could activate the layout into a disposable installed home and
still launch the server afterward.

## Repair proof

Every RID job now runs `server/test-staged-install.ps1` after platform signing
and before archiving. The proof imports the transaction module from the
packaged layout, validates the exact packaged server before activation,
activates into a disposable home while preserving user-owned content and
cutting over a disposable registration, validates the installed server with a
second complete five-tool handshake, confirms no rollback snapshot remains,
and removes the test home.

The workflow guard failed before the step was added. After repair,
`scripts/test-assemble-draft-release.sh`, actionlint, ShellCheck, and
`git diff --check` passed. A fresh local `osx-arm64` package then passed both
handshakes and the complete staged-install proof with build identity
`2c4ea930dcab4a67816884c0f24d64d9`.

This closes packaged activation as a workflow gate. Exact-candidate public
installer, upgrade, refusal, and uninstall evidence remains part of final
candidate validation; this focused proof does not claim those separate gates.
