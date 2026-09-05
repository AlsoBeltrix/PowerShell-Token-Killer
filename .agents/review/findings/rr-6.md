# rr-6: Windows SIEM deployment omits private permissions

**Severity:** HIGH — generated configuration is rejected by the receiver.
**Status:** CLOSED — native CI and both signed Windows candidate workflows pass.
**Scope:** `siem/manage.ps1`, `siem/test-manage.ps1`.

Both Windows jobs in candidate run `33930714689` passed signing, packaged
activation, the 32-check product/Defender proof, and receiver package smoke.
The mini-SIEM workflow then stopped with
`siem_receiver_configuration_invalid: config_protection`. Mac and Linux passed;
draft assembly was skipped and no versioned release was created.

`Set-PrivateDirectory` and `Set-PrivateFile` in the shipped manager only set
Unix modes. They omit Windows ownership/DACL setup. `Set-ServicePathOwner`
also returns immediately for the current identity. The receiver requires a
current-user-owned, protected DACL with exactly one non-inheriting allow ACE
giving that user FullControl. Its refusal is correct and remains unchanged.
The alternate identity handoff's additive/inheritable grants must likewise
satisfy that same owner-only contract for the selected service identity.

Under standing known-broken repair authority, establish exact Windows private
descriptors and preserve them during explicitly authorized identity handoff.
Keep Unix behavior, receiver admission, ownership consent, and separation of
service-owned secrets/data from installer-owned program/manifest/service files.
Leave concurrent unrelated harness edits untouched.

The lifecycle regression checks actual generated config/TLS files, data/witness
directories, manifest, and service definition against the receiver's exact
Windows owner/DACL/ACE contract. It also adds an extra reader to a disposable
config, requires rejection, and restores the original descriptor. This suite
runs in ordinary CI and before release signing. Land the guard before the
production fix for native red evidence; record native red/green before closure.

## Red proof and repair

Guard-only commit `8ff9949` produced native Windows failure in CI run
`33932859920`, job `101214936893`: `Windows private ACL contract failed` on the
newly generated `manifest-safety/config` directory. Linux and Mac lifecycle
jobs passed. This is an executed new guard against the unfixed manager, not a
static source-text assertion.

The repair adds one Windows ACL helper: current-user ownership plus a protected
DACL containing exactly one non-inheriting FullControl allow ACE. Private
directories/files call it; Unix modes are unchanged. Authorized identity
handoff enumerates/rejects reparse points before mutation, transfers deepest
children before parents with the existing `icacls` owner operation, verifies
the resulting owner, then writes the same exact DACL for that identity. The
existing consent gate and service-owned-path boundary are unchanged.

The Windows lifecycle guard additionally exercises the handoff path using a
differently-cased name for the same test SID. It proves the explicit-consent
refusal and the owner-only result for a nested tree without transferring files
away from the test account. This is not real cross-account service acceptance.
Local Mac lifecycle checks and the SIEM solution pass after the repair;
`git diff --check` passes for this slice. Native Windows green and the next
exact packaged candidate remain required.

Native green: fix commit `a57ae57` passed Windows job `101215931624` in CI
`33933206344`: 357 receiver tests, `WINDOWS PRIVATE DEPLOYMENT ACL TEST PASSED`,
complete deployment lifecycle, and native package build/verification. The extra
reader and consent assertions executed in that pass. Mac and Linux SIEM jobs
also passed. Signed-candidate run `33933424682` now builds that exact commit.

Signed-candidate run `33933424682` subsequently passed both Windows jobs
(`101216561003`, `101216561118`): exact private-ACL lifecycle guard, signed
packaged activation, all 32 product/Defender checks, and all three packaged
SIEM operator workflows. The original `config_protection` refusal is gone
without relaxing receiver admission. This closes the finding; real
cross-account handoff and external-SIEM product acceptance are not claimed.
