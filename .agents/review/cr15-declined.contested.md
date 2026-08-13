# cr15 declined candidates

Reviewer generation provenance and the invalid-envelope recovery outcome are
recorded in `.agents/review/index.md`. These candidates were independently
triaged from the review text; neither is an admitted defect.

## cr15-2 — packaged PDB

Declined. The published `v0.3.0-rc.1` osx-arm64 receiver archive does include
`PtkSiemReceiver.pdb`, but its embedded SourceLink document map is
`/Users/runner/work/PowerShell-Token-Killer/PowerShell-Token-Killer/*` to the
public GitHub repository at exact public source commit
`0c8ed87635ef37db548d086ada78a2020c4b390f`. It discloses no owner workstation
path or private source. A locally source-built layout naturally carries the
builder's own path, already known to that operator. The guide says the archive
contains the named product/docs/license material; it never promises an
exclusive closed file set, and self-contained .NET runtime files necessarily
extend that list. Removing useful symbols or inventing a closed-set manifest
has no demonstrated product failure to repair.

## cr15-3 — ARM64 ordinary CI

Declined. The approved S8 plan explicitly requires ordinary native package
build/verification on Ubuntu, Windows, and macOS, then closes only after the
five-RID signed release workflow is green. That exact contract passed hosted
run `31650818998` for ordinary CI and exact-head run `31654609624` for all five
native RIDs, including the ARM64 protobuf repair. Adding two costly runners to
every ordinary CI invocation is a coverage-policy change beyond the approved
plan, not an observable defect in the landed S8 implementation.
