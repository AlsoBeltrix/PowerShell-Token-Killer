# rr-2: public no-clone install omitted required installer modules

**Severity:** HIGH
**Status:** FIXED in the installer-bundle slice after `fa3d476`
**Scope:** `README.md`, `.github/workflows/release.yml`, release assembly helpers

## Finding

The README described its public install as requiring no repository clone, but
the command invoked `scripts/install.ps1` from a checkout. Downloading that
script alone was not a viable substitute: it imports
`ptk_install_transaction.psm1` and `ptk_build_provenance.psm1` from the same
directory. A clean unaffiliated-user environment therefore had no runnable
public bootstrap despite the documented claim.

## Repair proof

The release workflow now builds fixed-name `ptk-installer.zip` containing
exactly the installer and its two imported modules, adds its SHA-256 to
`SHA256SUMS`, and includes it with the ten native product/receiver archives.
Draft assembly recalculates and verifies all eleven hashes, requires one exact
manifest entry per artifact, and passes only the validated artifacts plus
`SHA256SUMS` to GitHub.

The README downloads the bundle and checksum manifest, verifies the bundle
before extraction or execution, and removes its temporary directory in a
`finally` block. `scripts/test-assemble-draft-release.sh` proves the exact
bundle contents and checksum, duplicate-bundle refusal, incomplete and
incorrect manifest refusal before the GitHub CLI boundary, exact upload set,
existing-release refusal, and release-query failure closure. The new guards
were observed failing before their repairs.
