# r806 self-review pass — candidates declined at intake

Owner-requested own pass (single-agent mode) over `d440234..32c444d`,
2026-08-06. Per the codereview playbook, declined reasons are written down
even when one mind holds both roles. Each candidate below was examined and
declined; none is admitted work.

- **r806-d1 — `SupervisorCallFilter.Refusal()` carries no structured
  content** (`server/PtkMcpServer/SupervisorCallFilter.cs:127–137`). The
  lifecycle-refusal result sets `isError=true` and text only, so the one
  refusal a structured-content client cannot read by disposition is the
  supervisor-shutdown one. Declined: no material observable failure —
  `isError=true` already carries the verdict on that path, and the absent
  `safe_to_resubmit=true` can only suppress a resubmit against a
  shutting-down supervisor.
- **r806-d2 — `executed=true` for `outcome_unknown`/`failed` dispositions**
  (`ToolOutcome.cs:85`). "Executed" reads as a claim the work ran where PTK
  can only say it is not a proved non-start. Declined: deliberate,
  documented (`ToolOutcome.cs:82–84`), and test-pinned semantics
  (`ToolOutcomeTests.cs:74–92`); the load-bearing field
  (`safe_to_resubmit`) is correct in every case; naming taste is not an
  observable failure.
- **r806-d3 — `StateAsync`'s `.Single()` can throw on a concurrent close**
  (`WorkerSupervisor.cs:124–125`). A session closed between `StateAsync`
  and `List()` makes `Single()` throw `InvalidOperationException`, escaping
  the typed catch list. Declined: pre-existing code outside the pinned
  range (only the return wrapper changed in this range), and the escape
  surfaces as a generic SDK error, not a false verdict.
- **r806-d4 — UNC/edge `HOMEDRIVE` derivation in the uninstall child
  environment** (`server/direct-product-proof.ps1:284–287`). Declined: the
  destructive check is documented for local throwaway homes; no observable
  failure on supported use.
