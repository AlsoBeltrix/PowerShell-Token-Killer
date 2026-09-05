# rr-6: Windows SIEM deployment omits private permissions

**Severity:** HIGH — generated configuration is rejected by the receiver.
**Status:** REGRESSION GUARD ADDED; production repair pending.
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
