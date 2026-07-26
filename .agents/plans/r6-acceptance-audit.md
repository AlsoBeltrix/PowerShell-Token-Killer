# R6 Acceptance Matrix Audit

**Status**: all ten audit gaps closed and verified, 2026-07-26, on
`feature/mcp-resilience-r1`. The owner-approved action gating R7 is complete.

**Purpose**: the R6 exit gate in `.agents/plans/mcp-resilience.md` is
"Close the R6 acceptance matrix before R7", and the matrix under
`## Acceptance matrix` had no map from acceptance line to covering test. This
file is that map plus the resulting gap list. It is the only place that map
lives; the matrix itself stays canonical in the plan and is not restated here
beyond an abridged claim per row.

**Method**: static audit. Test identities were enumerated from
`server/PtkMcpServer.Tests`, `server/PtkMcpGuardian.Tests`, and
`server/PtkGuardianArchitecture.Tests` (2,201 distinct method identities), then
matched against each matrix claim by concept search, with candidate test bodies
read wherever a name alone would not settle the question. **No suite was re-run
for the initial audit** — a row marked COVERED means a test exists that asserts
the claim, not that it was observed passing in that pass. Every corrective gap
slice was subsequently mutation-proven where required and the final combined
macOS battery is recorded in `.agents/machines.md`.

**How to read a row**

- `COVERED` — a named test asserts the claim, and its body was checked or its
  identity is unambiguous.
- `PARTIAL` — part of the claim is asserted and a named part is not. The note
  says which part is missing; every PARTIAL is also an entry in the gap list.
- `GAP` — no test asserts the claim.
- `SUPERSEDED` — a later owner decision removed the claim's subject from R6
  scope. Not a gap; must not be treated as one.
- `R7` — the claim describes R7 deliverables and cannot close before R7 exists.

Claim IDs decompose bullets that bundle several independently provable
properties. Section letters follow the matrix's own section order.

---

## Cross-cutting findings

**F1 — RESOLVED 2026-07-26 (G7): six of the seven now run everywhere.** The
finding as originally written is kept below because its reasoning still governs
how this suite is read — a platform-gated identity reports green without
executing, so a cross-platform run proves nothing about it — and one identity
remains deliberately gated. The cause turned out to be a test-harness interface
loss, not platform behaviour; see G7.

**F1 (as found) — Six real-apphost composition identities are Windows-only and
pass vacuously elsewhere.** In `ProductionGuardianCompositionTests` the following
open with `if (!OperatingSystem.IsWindows()) return;`, so on macOS and Linux
they report green without executing anything:
`Windows_composition_retains_real_decoded_terminal_on_loss` (and the two
`Windows_composition_classifies_real_*_loss` cases that delegate to it),
`Windows_composition_requires_explicit_repair_after_ambiguous_reset`,
`Windows_composition_recovers_a_real_host_on_the_same_public_connection`,
`Windows_composition_recovers_after_replacement_dies_during_startup`,
`Windows_composition_keeps_a_real_job_tombstone_and_sealed_output`,
`Windows_private_host_ignores_the_transitional_idle_watchdog`. This is the
`r6x-2` lesson as a structural fact rather than an incident: a green
cross-platform run says nothing about these seven identities off Windows. Six
sibling identities *are* cross-platform and branch internally
(`Composition_seals_a_real_background_job_artifact_for_handle_recovery`,
`Composition_serves_the_real_private_host_before_public_initialize`,
`Composition_opens_a_dynamic_session_on_the_real_private_host`,
`Composition_isolates_one_alias_worker_crash_from_a_second_alias`,
`Composition_never_replays_a_real_effect_when_the_worker_dies`,
`Composition_freezes_package_session_and_guardian_owned_state`); one is
Unix-only (`Unix_composition_recovers_real_host_and_descendants_on_the_same_public_connection`,
gated `if (OperatingSystem.IsWindows()) return;`, so it runs on macOS and
Linux).

**F2 — The matrix predates the lazy-load amendment and still demands frozen
template bootstrap.** The owner's 2026-07-24 amendment (recorded in
`.agents/plans/mcp-resilience.md` R6, line ~1017) removed template bootstrap
from R6: a recovered alias returns as a sound empty runspace, `ptk_session open`
is dynamic-only, and the wire contract's template fields stay inert. Matrix rows
B3 and part of B4 describe replaying exact frozen bootstrap bytes into a
recovered generation, which production no longer does. The code path still
exists and is still tested
(`SessionRecoveryStateMachineTests.Eligible_template_waits_for_confirmed_death_then_restores_exact_frozen_bytes_once`),
but production always passes empty bytes. These rows are marked SUPERSEDED, not
covered and not gaps. **The matrix text itself should be amended so a later
agent does not try to close them** — that amendment is an owner call, listed in
the gap list as G9.

**F3 — Both containment graces exist; the plan's `timeoutContainmentGrace` is
plan vocabulary, not a code identifier.** (Corrected 2026-07-26 — an earlier
revision of this audit wrongly recorded the worker grace as absent. It was
searched for as a C# camelCase identifier only; it lives in the native sources
and the frozen contract under snake_case names.)

- **Worker grace** (`timeoutContainmentGrace`, post-launch startup failure and
  post-start execution timeout): `PTK_CONTAINMENT_DEADLINE_MILLISECONDS 10000`
  and `PTK_TERM_TO_KILL_MILLISECONDS 2000` in
  `server/PtkMcpGuardian/Native/ptk_containment_broker.c`, compiled to
  `PtkContainmentBroker` by `server/PtkMcpServer/PtkMcpServer.csproj`.
- **Host grace** (`hostContainmentGrace`, outer host-generation teardown): the
  *separate* pair of identically named `#define`s in
  `server/PtkMcpGuardian/Native/ptk_guardian_broker.c`, plus the managed
  guardian-side deadline `GuardianHostLifecycleController.HostContainmentGrace`
  (10 s).
- Both values are independently pinned in the frozen
  `server/Contracts/ResilienceR0/contract.json` as `host_containment_grace_ms`
  and `timeout_containment_grace_ms`, asserted by
  `McpResilienceR0ContractTests.Containment_native_and_adapter_pins_are_closed`.

The "never borrows" clause therefore **holds by construction**: two distinct
constants in two distinct translation units producing two distinct binaries,
with no symbol either could read from the other. The clause is worth keeping in
the plan precisely because both values are 10 000 ms — a borrow would be
invisible to any test that only checked the number.

G1 closed the one narrow asymmetry: both brokers' independent constants are now
frozen by source regex against their production translation units.

**F4 — x64 Linux has never run this branch's battery.** The matrix requires the
complete repository battery and stdio handshake on macOS, x64 Linux, and
Windows. macOS is green at `15568a0`; Windows Guardian is 496/496 at `d431a2c`
(one commit behind, Unix-only delta); Linux has not been run at any head on this
branch. See G8.

**F5 — The audit's own blind spot.** Coverage here is per-claim, not per-mutation.
The plan's separate `## Required mutation proofs` list (38 items) was **not**
audited — no map exists from mutation number to the guard it should redden. That
is a distinct gate from the acceptance matrix and is out of scope for this pass;
see "Out of scope" below.

**F6 — Guardian-owned tool catalog makes several claims structural.** The public
tool catalog is served by `GuardianMcpApplication.HandleListToolsAsync` from a
frozen guardian-side list with no host input, so "a host event cannot change the
catalog" holds by construction rather than by a dedicated assertion. Rows that
rely on this say so explicitly rather than claiming a test that does not exist.

---

## Section A — Public connection and host recovery

| ID | Claim (abridged) | Status | Evidence |
|----|------------------|--------|----------|
| A1.1 | Initialize once, list tools, kill an idle host, call a real tool successfully on the replacement | COVERED | `ProductionGuardianCompositionTests.Unix_composition_recovers_real_host_and_descendants_on_the_same_public_connection` (macOS + Linux); `Windows_composition_recovers_a_real_host_on_the_same_public_connection` (Windows only, F1) |
| A1.2 | Same guardian PID, stdin/stdout, public request-ID domain, and initialization survive the kill | COVERED | `ResilienceFakeGuardianTests.Loss_after_ready_snapshot_but_before_private_write_is_proved_not_started` asserts `AssertPublicIdentityUnchanged(harness, guardianPid, publicInput, publicOutput)` at each barrier; `Guardian_private_request_ids_remain_monotonic_across_host_generations`; `GuardianHostSupervisorTests.Private_request_ids_remain_monotonic_across_replacement` |
| A1.3 | `ptk_state` is readable *while* the host is recovering | COVERED | `GuardianHostSupervisorTests.State_polling_is_guardian_local_and_scheduler_inert`; `ResilienceFakeGuardianTests.State_snapshot_that_wins_the_exit_observer_race_is_atomically_recovering` |
| A2.1 | EOF, exit, reader and writer failures racing start exactly one recovery | COVERED | `GuardianHostLifecycleControllerTests.Racing_loss_sources_begin_one_containment_with_one_exact_deadline`; `Racing_contract_mismatch_and_exit_always_stop_one_containment` |
| A2.2 | Public stdout remains open and valid throughout | COVERED | `ResilienceFakeGuardianTests.Disposable_guardian_preserves_public_pipe_and_delivery_truth_across_host_loss`; harness-wide `AssertPublicStdoutIsJsonRpcOnly`; `Already_closed_public_stdout_is_safe_during_harness_construction` |
| A3.1 | `host_containment_unconfirmed` is returned at exactly `hostContainmentGrace` | COVERED | `GuardianHostLifecycleControllerTests.Containment_deadline_is_exact_and_late_confirmation_starts_one_replacement`; `GuardianHostSupervisorTests.Unconfirmed_containment_is_durably_audited_with_the_old_identity` (fake clock advanced by exactly `HostContainmentGrace`) |
| A3.2 | The host grace never borrows `timeoutContainmentGrace` | COVERED (structural + source-pinned) | Two separate constants in two separate native translation units — F3. Values pinned independently by `McpResilienceR0ContractTests.Containment_native_and_adapter_pins_are_closed` (`host_containment_grace_ms` vs `timeout_containment_grace_ms`); host broker source frozen by `UnixGuardianBrokerIntegrationTests.Native_source_freezes_the_liveness_registry_and_reaping_boundary`; worker broker source frozen by `UnixWorkerProcessLauncherTests.Native_source_freezes_the_worker_broker_grace_constants` — G1 closed |
| A3.3 | No replacement starts before later confirmed old-tree death | COVERED | `GuardianHostSupervisorTests.Unconfirmed_containment_blocks_every_replacement`; `GuardianHostLifecycleControllerTests.Containment_deadline_is_exact_and_late_confirmation_starts_one_replacement`; `Failed_recovery_backoff_begins_only_after_confirmed_death` |
| A4.1 | Replacement contract/build mismatch is refused | COVERED | `GuardianHostSupervisorTests.Contract_mismatch_during_recovery_is_permanent` (nonretryable, no phase/attempt/gate metadata); `Contract_mismatch_during_prewrite_loss_beats_retry_guidance`; `GuardianHostLifecycleControllerTests.Contract_mismatch_and_identity_exhaustion_are_internal_permanent_terminals`; `GuardianHostClientTests.Initialize_rejects_manifest_pin_mismatch_before_read_or_write` |
| A4.2 | The refusal never changes the live public tool catalog | COVERED (structural) | Catalog is guardian-owned and frozen — F6; `GuardianMcpApplicationTests.Real_stream_transport_uses_only_the_frozen_contract_and_dispatcher`; `ToolSchemaConformanceTests` |
| A5.1 | A complete decoded terminal is delivered exactly once | COVERED | `ResilienceFakeGuardianTests.Loss_after_ready_snapshot_but_before_private_write_is_proved_not_started` (decoded-terminal leg, `ResponseCount == 1`); `ProductionGuardianCompositionTests.Windows_composition_retains_real_decoded_terminal_on_loss` (Windows only, F1); `GuardianCallDeliveryTrackerTests.Stop_blocks_new_transitions_but_preserves_a_predecoded_terminal_for_one_delivery` |
| A5.2 | Partial responses and effect-before-response crashes are `outcome_unknown` | COVERED | `R3GuardianAppHostIntegrationTests.Partial_private_response_is_one_nonretryable_outcome_unknown_terminal`; `GuardianHostSupervisorTests.Loss_after_private_write_is_outcome_unknown_and_never_replayed`; `GuardianAuditCallTests.Postwrite_loss_is_terminally_audited_as_outcome_unknown`; `R3FakeHostTests.Partial_response_has_one_effect_and_never_decodes_a_terminal` |
| A5.3 | Unwritten calls are definitely not started | COVERED | `ResilienceFakeGuardianTests.Loss_after_ready_snapshot_but_before_private_write_is_proved_not_started` (`backend_lost_before_dispatch`, zero received files); `GuardianAuditCallTests.Prewrite_refusal_is_proved_not_started_with_or_without_authorization`; `ProductionGuardianCompositionTests.Windows_composition_classifies_real_prewrite_loss` (Windows only) |
| A5.4 | None of the three is resent | COVERED | Same tests assert `ResponseCount == 1` and unchanged effect-file counts after the replacement is ready; `ResilienceFakeGuardianTests.Duplicate_private_response_kills_its_generation_and_cannot_poison_the_next_call`; `Invalid_private_response_fails_the_generation_without_replay` |
| A6.1 | Calls refused during recovery are never queued | COVERED | `GuardianHostSupervisorTests.State_polling_is_guardian_local_and_scheduler_inert`; `Failed_generations_open_a_bounded_circuit_without_poll_driven_probes`; refusal legs of `ResilienceFakeGuardianTests.Loss_after_ready_snapshot_but_before_private_write_is_proved_not_started` return immediately rather than parking |
| A6.2 | Exactly the four proved-no-start codes carry `retryable=true` | COVERED | `PublicRecoveryMatrixTests.Retryability_delay_and_attempt_bounds_are_exact`; `PublicRecoveryMatrixTests.Every_detail_phase_and_gate_combination_matches_the_frozen_closed_table` |
| A6.3 | Each refusal exposes matching phase, attempt, poll delay, readiness gate | COVERED | `PublicRecoveryMatrixTests.Every_detail_phase_and_gate_combination_matches_the_frozen_closed_table`; `ResilienceFakeGuardianTests.AssertRetryableHostGate`; `FrozenDefaultSessionStateTests.Invalidated_dispatch_target_carries_its_own_transitions_recovery_evidence` |
| A6.4 | Delay expiry permits only a state poll; a new request executes only after the poll reports the gate ready | COVERED | `GuardianHostSupervisorTests.Retry_delay_expiry_without_a_ready_state_snapshot_never_authorizes_dispatch` advances the fake clock by exactly the advertised `retry_after_ms`, deliberately skips the required state poll, and proves a fresh dispatch remains refused at the same session readiness gate without reaching the old host — G2 closed |
| A6.5 | Pre-write dispatch authorization rechecks readiness | COVERED | `ResilienceFakeGuardianTests.Loss_after_ready_snapshot_but_before_private_write_is_proved_not_started`; `SessionOperationAuthorityTests.Wire_deadline_is_rechecked_at_the_final_process_start_gate`; `FrozenDefaultSessionStateTests.Invalidated_dispatch_target_carries_its_own_transitions_recovery_evidence` |
| A6.6 | Every ambiguous/permanent error is nonretryable and `outcome_unknown` never produces retry instructions | COVERED | `PtkSharedContractsTests.Nonretryable_public_recovery_union_round_trips_without_retry_metadata`; `R3GuardianAppHostIntegrationTests.Partial_private_response_is_one_nonretryable_outcome_unknown_terminal`; `ResilienceFakeGuardianTests.AssertNonRetryable` |
| A7.1 | A fake client polls through multiple recovery phases without changing the scheduler, then submits exactly once after readiness | COVERED | `R3GuardianAppHostIntegrationTests.One_real_MCP_connection_survives_fake_host_crash_and_model_gated_retry`; `GuardianHostSupervisorTests.State_polling_is_guardian_local_and_scheduler_inert` |
| A7.2 | Death after the ready snapshot but before dispatch authorization starts no effect and returns new recovery guidance | COVERED | `ResilienceFakeGuardianTests.Loss_after_ready_snapshot_but_before_private_write_is_proved_not_started` |
| A7.3 | A loss after private write starts returns `outcome_unknown` with no retry guidance | COVERED | Same test, `write_started` leg; `GuardianHostSupervisorTests.Loss_after_private_write_is_outcome_unknown_and_never_replayed` |
| A8.1 | Foreground/background invoke and backend-dependent job control name their exact session | COVERED | `PublicRecoveryMatrixTests.Every_detail_phase_and_gate_combination_matches_the_frozen_closed_table`; `GuardianHostSupervisorTests.Host_recovery_refusal_uses_the_selected_session_gate` |
| A8.2 | Lifecycle repair names only the host | COVERED | `GuardianHostSupervisorTests.Host_recovery_refusal_for_public_reset_uses_the_host_gate`; `Public_reset_uses_the_host_gate_and_binds_exact_control_facts`; `Public_session_lifecycle_uses_the_host_gate_and_binds_exact_control_facts` |
| A8.3 | Guardian-local operations emit no gate | COVERED | `GuardianHostSupervisorTests.Public_session_list_is_guardian_local_and_uses_projected_state`; `Public_output_reads_searches_and_reports_guardian_local_artifacts`; `State_polling_is_guardian_local_and_scheduler_inert` |
| A8.4 | A recovering session cannot make its own restart wait for session readiness | COVERED | `GuardianHostSupervisorTests.Host_recovery_refusal_for_session_restart_uses_the_host_gate` |
| A9.1 | Old-generation responses and events cannot complete a request or mutate state | COVERED | `GuardianHostLifecycleControllerTests.Old_generation_callbacks_are_inert_after_replacement`; `GuardianCallDeliveryTrackerTests.Stale_old_host_loss_is_inert_and_cannot_block_the_bound_generation`; `GuardianHostSupervisorTests.Stale_host_job_capability_is_refused_on_the_replacement_generation` |
| A9.2 | Forged diagnostic JSON cannot complete a request or mutate state | COVERED | `GuardianHostClientTests.Forged_operation_event_never_reaches_the_handler_or_mutates_sequence_state`; `GuardianJobCapabilityRegistryTests.Forged_registration_cannot_activate_or_cancel_the_reserved_owner`; `WorkerProcessExitTests.Unknown_server_detail_is_generic_and_cannot_inject_data` |

## Section B — Generation and state restoration

| ID | Claim (abridged) | Status | Evidence |
|----|------------------|--------|----------|
| B1.1 | Host generations never reuse across failed starts | COVERED | `GuardianHostLifecycleControllerTests.Launch_proved_no_child_consumes_generation_and_never_reuses_it`; `SessionRecoveryStateMachineTests.Reused_generation_from_injected_allocator_fails_closed` |
| B1.2 | Worker generations remain monotonic across host restart | COVERED | `GuardianIdentityAllocatorTests.Worker_sequences_are_alias_local_resume_after_seed_and_preserve_gaps`; `Worker_allocations_are_concurrently_unique_per_alias`; `WorkerPrivateHostRuntimeTests.Reopen_reuses_the_declared_binding_and_advances_the_generation` |
| B1.3 | A granted-but-unused worker-create capability leaves a visible generation gap; the next attempt uses a greater value | COVERED | `SessionRecoveryStateMachineTests.Failed_prepare_consumes_generation_and_next_attempt_uses_a_gap`; `Undispatched_attached_restart_source_loss_recovers_after_consumed_generation_gap`; `Abandoned_cold_open_preserves_state_fences_stale_leases_and_consumes_generation` |
| B2.1 | No replacement begins before confirmed old-tree death | COVERED | See A3.3 |
| B2.2 | Unconfirmed death keeps state/output reads available but blocks effects and generation advance | COVERED | `GuardianHostSupervisorTests.Lost_background_job_reads_stay_guardian_local_during_containment`; `Unconfirmed_containment_blocks_every_replacement`; `GuardianHostLifecycleControllerTests.Unconfirmed_terminal_shutdown_retains_identity_and_never_restarts` |
| B3 | Exact frozen bootstrap bytes/digest used once per recovered generation even after the source file is edited, replaced, deleted, or symlinked | SUPERSEDED | Lazy-load amendment — F2. The remaining machinery is still guarded by `SessionRecoveryStateMachineTests.Eligible_template_waits_for_confirmed_death_then_restores_exact_frozen_bytes_once`, `Concurrent_execute_calls_invoke_bootstrap_at_most_once`, `Deterministic_bootstrap_failure_faults_once_without_retry`, `First_shutdown_clears_guardian_lifetime_frozen_bootstrap_bytes`, but production passes empty bytes and never reads a source file |
| B4.1 | Arbitrary warm mutations, connections, and jobs do not return after recovery | COVERED | `GuardianHostSupervisorTests.Recovered_host_persists_the_declared_warm_state_loss`; `Ready_and_recovered_hosts_are_durably_distinguished`; `FrozenDefaultSessionStateTests.Recovered_ready_host_persists_warm_state_loss_for_the_guardian_lifetime`; real-apphost warm-state distinction in `ProductionGuardianCompositionTests.Composition_isolates_one_alias_worker_crash_from_a_second_alias` |
| B4.2 | "Bootstrap baseline returns" | SUPERSEDED | Under F2 the baseline *is* an empty sound runspace; the surviving obligation is B4.1 |
| B4.3 | Dynamic sessions return empty; closed/cold aliases remain closed/cold | COVERED | `FrozenDefaultSessionStateTests.Closed_dynamic_alias_is_declared_cold_in_the_next_manifest_and_reopen_flips_it_back`; `Declared_dynamic_alias_projects_cold_until_its_first_grant_binds_a_worker`; `WorkerPrivateHostRuntimeTests.Initialization_restores_a_ready_dynamic_binding_and_serves_both_aliases` |
| B5.1 | Ambiguous lifecycle/bootstrap calls become `recovery_unknown` and are never replayed | COVERED | `SessionRecoveryStateMachineTests.Dispatched_unacknowledged_lifecycle_is_recovery_unknown_and_never_replayed`; `GuardianHostSupervisorTests.Ready_session_replacement_without_evidence_is_recovery_unknown`; `Ambiguous_reset_blocks_session_work_until_explicit_repair`; `FrozenDefaultSessionStateTests.Ambiguous_lifecycle_stays_blocked_until_an_authoritative_repair`, `Recovering_lifecycle_cannot_downgrade_an_ambiguous_alias`; real apphost: `ProductionGuardianCompositionTests.Windows_composition_requires_explicit_repair_after_ambiguous_reset` (Windows only, F1) |
| B5.2 | A stale expected generation refuses before effects | COVERED | `PrivateHostWorkerCreateCapabilitySourceTests.Stale_generation_and_wrong_control_type_are_rejected`; `WorkerOperationProtocolTests.Request_and_cancel_reject_stale_identity_generation_and_request_id`; `FrozenDefaultSessionStateTests.Stale_ready_lifecycle_cannot_replace_the_latest_grant` |
| B6.1 | Public job/output IDs never reuse | COVERED | `JobManagerTests.Shared_guardian_allocator_never_reuses_abandoned_or_failed_start_ids`; `Failed_start_does_not_reuse_its_public_job_id`; `PublicJobIdAllocatorTests.Concurrent_allocations_are_unique_and_gap_free` |
| B6.2 | Sealed output remains readable after loss | COVERED | `ProductionGuardianCompositionTests.Composition_seals_a_real_background_job_artifact_for_handle_recovery` (cross-platform, landed under `r6x-2` #3); `Windows_composition_keeps_a_real_job_tombstone_and_sealed_output` (Windows only) |
| B6.3 | Incomplete output and lost jobs are truthful tombstones | COVERED | `GuardianHostSupervisorTests.Replacement_job_list_merges_current_jobs_with_lost_tombstones`; `GuardianJobCapabilityRegistryTests.Lost_generation_becomes_a_session_scoped_containment_tombstone`; `OutputStoreTests.SealIncomplete_keeps_terminal_streams_labeled_and_forces_incomplete_state`, `Expiry_during_unlocked_read_reports_the_tombstone_state`; `GuardianOutputCapabilityRegistryTests.Host_loss_publishes_only_the_exact_nonempty_valid_prefix_as_incomplete` |
| B7.1 | An execution timeout returns its single terminal and confirms old-tree death before allocating the next generation | COVERED | `WorkerPreparedInvokeControllerTests.Deadline_expiry_terminalizes_a_committed_operation_without_runtime_cooperation`; `WorkerSessionRuntimeAdapterTests.Prepared_adapter_preserves_a_structured_execution_timeout_terminal`; `WorkerPrivateHostRuntimeTests.Execution_timeout_contains_the_worker_and_recovers_a_fresh_baseline`; `SessionRecoveryStateMachineTests.Retryable_attempt_failure_cannot_advance_before_confirmed_tree_death` |
| B7.2 | Recovery creates only the declared baseline and never reruns the timed-out operation | COVERED | Same test. Note the known non-mutation-provable ownership check recorded in `.agents/state.md` for head `02b924c` — removing it leaves the suite green because no call path can deliver a timeout terminal for a non-current slot |
| B7.3 | The timeout path is proven on the real apphost, not only the in-proc rig | COVERED | `ProductionGuardianCompositionTests.Composition_execution_timeout_recovers_a_fresh_declared_baseline_without_replay` drives a real foreground script past its deadline and proves one timeout terminal, confirmed old-process death before replacement readiness, a fresh generation and declared baseline with `WarmStateLost`, no surviving warm sentinel, and no replay |

## Section C — Availability, loops, and isolation

| ID | Claim (abridged) | Status | Evidence |
|----|------------------|--------|----------|
| C1.1 | Guardian-local state/list/output stay prompt during containment | COVERED | `GuardianHostSupervisorTests.Lost_background_job_reads_stay_guardian_local_during_containment`; `Public_output_reads_searches_and_reports_guardian_local_artifacts`; `Public_session_list_is_guardian_local_and_uses_projected_state` |
| C1.2 | …and during startup, every backoff delay, circuit-open, and half-open | COVERED | `GuardianHostSupervisorTests.State_polling_is_guardian_local_and_scheduler_inert` is a phase-parameterised theory that holds real supervisor states at Starting, Ready, an active Backoff, CircuitOpen, and HalfOpen; each row performs 100 public `ptk_state` reads without scheduling a delay, launching an attempt, or reaching a host operation. Backoff has one state/read path independent of delay duration; the circuit arrangement also drives the exact five-delay sequence |
| C1.3 | MCP `ping` and `tools/list` remain prompt in every phase | COVERED | `GuardianAppHostProcessSmokeTests.Apphost_serves_one_clean_MCP_connection_and_exits_on_input_eof` proves the real healthy apphost answers both methods. `R3GuardianAppHostIntegrationTests.One_real_MCP_connection_survives_fake_host_crash_and_model_gated_retry` holds containment unresolved on that same MCP connection and proves `ping` returns a result, `tools/list` returns the frozen contract, the host remains Recovering, and neither reaches the crashed private host |
| C2 | Fake-clock proof of the exact delay sequence, six-failure circuit, 60 s cooldown, one half-open attempt, 60 s stability reset | COVERED | `RecoveryCircuitMachineTests.Failure_table_schedules_exact_attempts_then_opens_the_circuit`, `Half_open_failure_reopens_for_sixty_seconds_with_next_ordinal`, `Half_open_loss_at_stability_boundary_starts_a_fresh_cycle`, `Explicit_stability_reset_makes_later_loss_a_fresh_immediate_attempt_one`, `Retry_after_uses_monotonic_ceiling_and_contract_clamp`; `GuardianHostLifecycleControllerTests.Six_confirmed_failed_generations_open_one_circuit_and_one_half_open_probe`, `Pre_stability_half_open_loss_reopens_the_circuit_after_slow_containment`; `SessionRecoveryStateMachineTests.Retryable_failures_use_exact_backoffs_six_failure_circuit_and_one_half_open` |
| C3 | A 100-cycle soak proves bounded processes, handles, FDs, readers, timers, buffers, audit reservations, and memory; identities remain monotonic | COVERED | `ProductionGuardianCompositionTests.Real_process_soak_bounds_os_and_guardian_resources_across_one_hundred_recoveries` runs 101 cross-platform real-apphost generations. At every settled generation it proves exactly one live private host; bounded same-process Windows handles or Unix FDs from a same-run warmup; fixed guardian clients, watcher sets, readers, scheduler entries, public buffers, call/output/job registries, active audit calls, and the one constant lifecycle audit reservation; retired real-process authorities are collectable after forced GC and post-GC heap stays under a same-run warmup ceiling. Host generations are exactly 1–101. `GuardianHostSupervisorTests.Attempt_watcher_bookkeeping_is_bounded_across_one_hundred_fake_recoveries` separately retains exact fake-resource disposal and monotonic private request-ID proof. See G6 |
| C4.1 | One worker crash affects only one alias | COVERED | `ProductionGuardianCompositionTests.Composition_isolates_one_alias_worker_crash_from_a_second_alias` (real apphost, cross-platform — landed with `r6acc-1`); `SessionRecoveryStateMachineTests.One_alias_failure_does_not_change_another_alias_circuit_or_generation`; `WorkerPrivateHostRuntimeTests.Failed_close_faults_only_its_alias`, `Failed_reset_faults_only_its_alias_and_clears_its_job_budget` |
| C4.2 | Host crash affects all live sessions but not the guardian connection | COVERED | `GuardianHostSupervisorTests.Host_loss_projects_last_known_ready_session_as_unavailable`; `GuardianHostSessionStateProjectionTests.Unavailable_host_removes_impossible_ready_session_claim`; `ResilienceFakeGuardianTests.Disposable_guardian_preserves_public_pipe_and_delivery_truth_across_host_loss` |
| C4.3 | Guardian death is connection-fatal and leaves no descendant | COVERED | `UnixGuardianBrokerIntegrationTests.Guardian_death_contains_every_creation_barrier`, `Guardian_death_interrupts_a_stalled_creation_protocol`, `Creation_barrier_matrix_is_exact` (Unix); `WindowsNestedJobResilienceIntegrationTests.Outer_close_kills_creation_time_nested_host_worker_and_descendant` (Windows) |
| C5.1 | Recovery never starts after intentional public EOF | COVERED | `ResilienceFakeGuardianTests.Public_eof_waits_for_an_active_recovery_loop_before_disposal`; `GuardianAppHostProcessSmokeTests.Apphost_serves_one_clean_MCP_connection_and_exits_on_input_eof`; `CanonicalLayoutPackageTests.Unix_layout_is_matched_and_the_packaged_guardian_accepts_public_eof` / `Windows_layout_…` |
| C5.2 | Idle policy never creates a restart loop; advancing a fake clock past every transitional idle interval on an open pipe preserves host/worker PIDs, generations, warm-state sentinel, and lifecycle-audit count | COVERED | `ProductionGuardianCompositionTests.Private_host_ignores_the_transitional_idle_watchdog` is cross-platform after G7 removed its vacuous Windows gate and supplied the platform launcher. `IdleWatchdogTests.Fires_once_the_idle_timeout_elapses` / `Does_not_fire_while_activity_keeps_arriving` retain the unit boundary. See G7 |

## Section D — Audit export compatibility

| ID | Claim (abridged) | Status | Evidence |
|----|------------------|--------|----------|
| D1.1 | One fake durable OTLP receiver accepts exact v1, v2, and v3 bodies | COVERED | `AuditOtlpSiemConformanceTests.Producer_fixture_serializer_emits_exact_current_v1_v2_and_v3_request_bytes`; `AuditSiemProducerCorpusTests.Tracked_corpus_is_the_exact_current_v1_v2_and_frozen_v3_producer_wire`; `AuditExportAcknowledgmentObserverTests.Valid_v1_v2_and_v3_reference_is_marked_before_checkpoint_and_finalized_only_afterward` |
| D1.2 | V3 preserves every prior OTLP attribute and adds exactly the four typed `ptk.host.*` attributes; null host identity fields are omitted | COVERED | `AuditV3CompatibilityTests.V3_readers_accept_frozen_vectors_and_otlp_adds_only_typed_host_attributes`, `Explicit_v3_serializer_matches_both_frozen_r0_vectors_byte_for_byte`, `V3_readers_reject_missing_extra_or_semantically_invalid_host_snapshots`, `Explicit_v3_serializer_rejects_invalid_host_identity_state_pairs`; `McpResilienceR0ContractTests.Audit_v3_vectors_have_exact_shape_hash_and_host_semantics` |
| D2.1 | A Collector fixture maps PTK OTLP logs into the frozen `splunk-hec-event.jsonl` request | COVERED | `McpResilienceR0ContractTests.Splunk_fixture_disables_transport_compression_for_exact_wire_vector` |
| D2.2 | A Sentinel adapter fixture maps the same record into Logs Ingestion JSON, Direct DCR, and custom-table shapes without truncation or type loss | COVERED | `McpResilienceR0ContractTests.Sentinel_static_vector_derives_every_column_from_raw_event` |
| D2.3 | `adapter-live-validation.json` records the pinned translator proof and the credentialed release checks offline CI cannot perform | COVERED (by design, offline) | `McpResilienceR0ContractTests.Containment_native_and_adapter_pins_are_closed` |
| D3 | Adapters tolerate identical at-least-once duplicates and fail their compatibility gate on any change to event ID, hashes, schema, host identity/generation/state, timestamp precision, or Unicode | COVERED | `AuditV3CompatibilityTests.V3_readers_reject_missing_extra_or_semantically_invalid_host_snapshots`; `AuditEventTests.Serialize_rejects_invalid_scalar_bounds_names_enums_and_unicode`; `AuditOtlpRecordMapperTests.Map_fails_closed_when_a_required_query_value_cannot_be_mapped_exactly`, `Map_rejects_v1_v2_version_shape_hybrids`; `AuditOtlpExportCompositionTests.Real_https_503_retries_identical_record_and_checkpoints_only_after_200` |
| D4.1 | PTK's anchor advances only at its configured durable OTLP endpoint, never because a downstream adapter accepted a transient queue | COVERED | `AuditOtlpHttpExporterTests.Wrong_auth_is_sent_only_to_the_configured_anchor_and_classified_nonretryable`, `Unknown_200_acknowledgment_is_retryable_with_the_same_record`; `AuditOtlpExportCompositionTests.Real_https_503_retries_identical_record_and_checkpoints_only_after_200` |
| D4.2 | Supported adapter versions are revalidated during R0 **and R7** | R7 | R0 half is pinned by `McpResilienceR0ContractTests.Containment_native_and_adapter_pins_are_closed`; the R7 revalidation cannot happen before R7 |
| D5 | The mini-SIEM conformance fixture can replace the fake receiver without changing PTK bytes, once the producer-owned v3 request-byte gate is satisfied | BLOCKED (known, parked) | Gate is deliberately closed — see `.agents/state.md` `## Open / Parked`. Receiver S1–S3 are implemented; `AuditOtlpSiemConformanceTests` covers the producer side. Not a new gap and not an R6 blocker |

## Section E — Platform, security, and compatibility

| ID | Claim (abridged) | Status | Evidence |
|----|------------------|--------|----------|
| E1.1 | Common deterministic suites pass on Windows, Linux, and macOS | COVERED | All three platforms run at `e5f67a9`/`c600325`: macOS green, x64 Linux green, Windows green except its 18 pre-existing ordinary-account cert/DPAPI/mTLS and parked-dialect failures, which predate this branch and are unchanged. G8 closed — see `.agents/machines.md` |
| E1.2 | Native tests hard-kill guardian/host/worker at the creation, initialize, bootstrap, ready, foreground-busy, and job-running barriers | COVERED | All six barriers have real-process proof. G7 made initialize cross-platform; G10 added a deterministic cross-platform descriptor-bootstrap barrier. Full per-barrier map below |

**E1.2 per-barrier map** (searched by mechanism, not by name — the barriers are
not named in test identities, which is what defeated the first pass):

| Barrier | Status | Evidence |
|---|---|---|
| creation | COVERED, exhaustively | `UnixGuardianBrokerIntegrationTests.Creation_barrier_matrix_is_exact` pins seven sub-barriers (`host_gated`, `before_pending`, `during_move`, `before_armed_ack`, `after_armed_ack`, `after_release_command`, `after_release`) and `Guardian_death_contains_every_creation_barrier` kills at each; `Guardian_death_interrupts_a_stalled_creation_protocol`; Windows `WindowsContainmentIntegrationTests.Suspended_worker_is_contained_before_entry_and_job_owner_kills_its_tree`, `Runnable_worker_enters_without_a_proof_resume` |
| initialize | COVERED | `ProductionGuardianCompositionTests.Composition_recovers_after_replacement_dies_during_startup` drives the real platform launcher and kills the replacement before ready on Windows, Linux, and macOS after G7 removed its vacuous Windows gate. Rig-level `WorkerPrivateHostRuntimeTests.A_worker_dying_during_initialization_recovers_once_ready` and `WorkerProcessClientTests.Exit_before_hello_refuses_launch_and_contains_once` retain the protocol edges |
| bootstrap | COVERED | `ProductionGuardianCompositionTests.Hard_kill_during_private_host_descriptor_bootstrap_closes_both_channels` launches the disposable fixture through the real platform launcher. The fixture enters production `PrivateHostBootstrapCapture` / `PrivateHostBootstrapNative`, signals only after the first inherited handle is owned, and blocks before the second; the test hard-kills there, confirms containment, event-channel EOF, and request-channel write failure. The barrier is deterministic rather than a sub-second observation race |
| ready | COVERED | `Unix_composition_recovers_real_host_and_descendants_on_the_same_public_connection` (macOS + Linux); `Windows_composition_recovers_a_real_host_on_the_same_public_connection` |
| foreground-busy | COVERED | `Composition_never_replays_a_real_effect_when_the_worker_dies` is cross-platform and kills the worker *from inside a live foreground `ptk_invoke`* — the script is `[System.Diagnostics.Process]::GetCurrentProcess().Kill()`. Windows adds three kill points around a real dispatch via the `RealDispatchBarrier` enum (`BeforeWriteAuthorization`, `WriteStarting`, `TerminalDecoded`) in `Windows_composition_classifies_real_prewrite_loss` / `..._possibly_written_loss` / `..._retains_real_decoded_terminal_on_loss` |
| job-running | COVERED | `Composition_seals_a_real_background_job_artifact_for_handle_recovery` (cross-platform); `Windows_composition_keeps_a_real_job_tombstone_and_sealed_output` |
| E2.1 | Windows proves creation-time outer containment, nested Job Objects, noninheritance, direct `NETWATCH-01` cleanup | COVERED | `WindowsNestedJobResilienceIntegrationTests.Outer_close_kills_creation_time_nested_host_worker_and_descendant`, `Disposable_probe_has_one_atomic_creator_and_no_job_handle_escape_path`; `WindowsProcessTreeSupervisorTests.Native_creation_flags_are_exact_and_suspension_is_proof_only`, `Native_production_has_one_atomic_create_and_no_fallback_or_sweep_escape_hatch`; `WindowsWorkerLifecycleIntegrationTests.Contained_worker_completes_lifecycle_with_silent_diagnostics`. Direct `NETWATCH-01` evidence is recorded in `.agents/machines.md` |
| E2.2 | Linux/macOS prove broker liveness cleanup, host-group ownership, pending/armed registration, start-identity fencing, direct-host reap, descendant exit confirmation, nonchild reaping, no old/new group overlap | COVERED | `UnixGuardianBrokerIntegrationTests.*`; `UnixWorkerProcessLauncherTests.Production_broker_launches_real_worker_only_after_both_registry_acks`; `PrivateHostUnixWorkerContainmentRegistryTests`; `ProcessTreeContainmentTests.Terminal_release_sweeps_escaped_orphans`, `Instantly_daemonized_orphan_is_reaped_by_escalation`, `Tree_kill_defeats_a_sigterm_trap`, `Fallback_survivor_requires_matching_incarnation`. G8 records complete macOS and x64 Linux execution |
| E2.3 | Guards fail if an R5 child misses the host group, a worker leaves before pending acknowledgment, or release precedes armed acknowledgment | COVERED | `UnixGuardianBrokerIntegrationTests.Creation_barrier_matrix_is_exact`, `Corrupted_start_identity_cannot_signal_a_live_sentinel`, `Native_source_freezes_the_liveness_registry_and_reaping_boundary` |
| E3 | Guardian stdout is exclusively valid MCP; all other output is bounded stderr/private diagnostics with no scripts, bootstrap, secrets, paths, raw environment, or exception text | COVERED | `ResilienceFakeGuardianTests.AssertPublicStdoutIsJsonRpcOnly` (applied at every barrier), `Structurally_invalid_public_json_is_recorded_and_the_next_response_is_drained`; `GuardianAppHostProcessSmokeTests.Apphost_rejects_relaxed_fake_host_flag_without_opening_stdout`; `GuardianMcpApplicationTests.Fake_host_startup_failure_is_stderr_only_and_never_opens_MCP`; `WorkerProcessExitTests.Unknown_server_detail_is_generic_and_cannot_inject_data`; `WorkerPreparedOperationCodecTests.Failures_retain_no_script_digest_identifier_or_inner_exception`; `AuditExportConfigurationTests.Sanitized_failures_never_echo_secret_values_paths_or_inner_exceptions`; `ScriptEvidenceStoreTests.Constructor_refuses_a_symlinked_evidence_root_without_disclosing_its_path` |
| E4.1 | Existing tool names/schemas/default successful outputs remain compatible; only frozen recovery/state fields and failures change | COVERED | `ToolSchemaConformanceTests`; `McpResilienceR0ContractTests.Public_recovery_and_end_state_tool_contract_are_exact`, `Public_state_schema_closes_identity_state_combinations`, `Public_state_schema_closes_state_phase_and_readiness_combinations`; `RawUsageTests.*` |
| E4.2 | Existing .NET, Pester, handshake, audit, output, release, and platform batteries remain green | COVERED | Same complete macOS, x64 Linux, and recorded Windows baseline as E1.1/G8; the required manual handshake passed on every exact-head validation recorded in `.agents/machines.md` |
| E5 | A local R7 package fixture proves one successful guardian-only cutover and failure at every payload-activation and per-harness registration boundary, each injected failure restoring byte-identical prior payload and registrations | R7 | Package *loading* is covered (`MatchedPackageLoaderTests.*`, `CanonicalLayoutPackageTests.*`, `CurrentMatchedPackageTests.*`); the cutover/rollback fixture is R7 work and cannot close before R7 |

---

## Gap list

Ordered by what blocks R7 first. Every item is either a missing guard or a
missing run; none requires new product behaviour except where stated.

**G1 — CLOSED 2026-07-26.** The worker broker's grace constants are now
source-pinned alongside the host broker's (A3.2, mutation proof #38).
`UnixWorkerProcessLauncherTests.Native_source_freezes_the_worker_broker_grace_constants`
reads the production `ptk_containment_broker.c` through `BrokerSourcePath()` and
freezes `PTK_TERM_TO_KILL_MILLISECONDS` at 2 000 ms,
`PTK_CONTAINMENT_DEADLINE_MILLISECONDS` at 10 000 ms, and
`PTK_POLL_MILLISECONDS` at 25 ms. Changing the containment deadline to 20 000 ms
reddened exactly this guard; restoring 10 000 ms returned it green.

**G2 — CLOSED 2026-07-26.** The retry-sequencing rule is now guarded by
`GuardianHostSupervisorTests.Retry_delay_expiry_without_a_ready_state_snapshot_never_authorizes_dispatch`.
The test advances the fake clock by exactly the advertised `retry_after_ms`,
deliberately performs no intervening state poll, and proves a fresh dispatch is
still refused at the unchanged session readiness gate without reaching the old
host. A mutation that treated elapsed fake time as dispatch authorization
reddened exactly the post-delay assertion (`host_recovering` became
`backend_lost_before_dispatch`); restoring the unconditional ready-state check
returned the guard green.

**G3 — CLOSED 2026-07-26.** The cross-platform real-apphost identity
`ProductionGuardianCompositionTests.Composition_execution_timeout_recovers_a_fresh_declared_baseline_without_replay`
now proves the single timeout terminal, one execution, confirmed old-process
death before replacement readiness, a fresh PID and generation with
`WarmStateLost`, an absent warm-state sentinel, and no replay.

The guard exposed a production defect hidden by the in-proc rig. Prepared
foreground invokes share one absolute host/worker deadline, so the host's
cancellation observer could win after the commit write boundary and bypass
response-status-based containment, leaving the runaway worker alive and
projected `Ready`. Tracking that boundary and containing on host-deadline
cancellation killed the worker, but ordinary disposal marked the exit
intentional and suppressed `Fatal`, leaving the dead generation projected
`Ready`. Recovery-specific containment now leaves the process monitor armed;
confirmed death completes `Fatal`, and the existing alias death watch owns
replacement. The baseline failed first with the old process still alive, then
with no fresh generation after ordinary disposal; the final identity passes.

G10 full verification reopened G3 at exact pre-G10 head `88482df`: the opposite
deadline race could let `RunspaceHost` return a structured destructive timeout
before host cancellation won. `SessionRuntime` rendered that `InvokeResult` to
text, the prepared controller emitted `Completed`, and the MCP response carried
the timeout sentence under `isError:false`; classifying that sentence would
have been an invalid fix. `WorkerPreparedRuntimeResult.ExecutionTimedOut` now
preserves only the machine-readable `TimedOut && WarmStateLost` condition, and
the controller maps it to the `prepared_execution_timed_out` `Expired`
terminal. Non-destructive queue expiry remains distinct. The deterministic
adapter guard failed before the fix (`Expected: Expired; Actual: Completed`)
and passed after restoration; the real-apphost identity then passed 5/5.

The next combined full run exposed the last committed-deadline liveness hole.
After commit, the controller deadline observer deliberately handled no state,
so it could emit no terminal unless the runtime returned or the host observer
canceled transport first. Under full-suite load neither won: the real timeout
request waited for 180 seconds and the identity ended in test cancellation.
`WorkerPreparedInvokeController` now treats the same absolute deadline as an
independent committed terminal authority. It publishes the one
`prepared_execution_timed_out` `Expired` terminal, then requests runtime
cancellation; the guardian receives `TimedOut` and owns containment even if the
runtime never cooperates. The deterministic
`Deadline_expiry_terminalizes_a_committed_operation_without_runtime_cooperation`
guard failed in one second before the fix and passed after it. The focused
controller/runtime group passed 13/13, the real-apphost identity passed another
5/5, and the combined full battery passed.

The product-visible consequence remains deliberate: execution timeout destroys
the alias's warm state (owner-approved 2026-07-15), which R7's cutover notes must
carry.

**G4 — CLOSED 2026-07-26.** The existing
`GuardianHostSupervisorTests.State_polling_is_guardian_local_and_scheduler_inert`
guard is now a phase-parameterised theory over Starting, Ready, an active
Backoff, CircuitOpen, and HalfOpen. Test-only startup holds freeze the initial
and half-open attempts before hello so the read is exercised in the intended
state rather than racing through it. Every row performs 100 public `ptk_state`
reads and proves the projected state/readiness, scheduler count, attempt count,
and zero host operations remain unchanged.

The mutation proof restricted `ptk_state` to a Ready host. Starting, Backoff,
CircuitOpen, and HalfOpen reddened while Ready stayed green; restoring the
unconditional guardian-local read returned all five rows green.

**G5 — CLOSED 2026-07-26.** `ping` was untested anywhere in the
repository; `GuardianAppHostProcessSmokeTests.Apphost_serves_one_clean_MCP_connection_and_exits_on_input_eof`
now asserts the **real apphost** answers it with a result and no error. It does
— the SDK handles the method — so this guards a working behaviour rather than
fixing a defect, which matters because an unanswered `ping` is how a client
concludes the server is gone and drops every tool in the session.

The recovery residual is now closed in
`R3GuardianAppHostIntegrationTests.One_real_MCP_connection_survives_fake_host_crash_and_model_gated_retry`.
The test holds containment proof unresolved, then proves `ping` returns a result
without an error and `tools/list` returns the full frozen contract on the same
public MCP connection. A following state read remains `Recovering`, and the
crashed private host's operation count is unchanged, so neither method waited
for recovery or crossed the private boundary. The SDK/contract handlers contain
no host-phase branch; G4 separately pins truthful local state reads in Starting,
Backoff, CircuitOpen, and HalfOpen.

**G6 — CLOSED 2026-07-26.** The original soak is now named
`Attempt_watcher_bookkeeping_is_bounded_across_one_hundred_fake_recoveries`, so
its in-proc scope is explicit. The companion
`Real_process_soak_bounds_os_and_guardian_resources_across_one_hundred_recoveries`
runs 101 real private-host generations on the platform launcher and directly
bounds child processes, Windows handles or Unix FDs, guardian readers/timers,
public and guardian-owned buffers/registries, active audit calls, the fixed
server-lifecycle audit reservation, and managed per-generation ownership. Its
handle/FD and post-GC heap ceilings come from the same run's first ten settled
generations rather than fixed machine-sized values; every retired real-process
authority must also be collectable. Exact generations 1–101 plus the fake
soak's private request IDs preserve the identity proof. Mutation proof retained
old clients in the supervisor: the real soak failed at generation 2 with two
owned clients, then passed all 101 generations after restoration.

**G7 — DONE 2026-07-26, and it took F1's whole class with it.** Seven identities
returned vacuously off Windows; six now run everywhere
(`Composition_classifies_real_prewrite_loss`,
`..._possibly_written_loss`, `Composition_retains_real_decoded_terminal_on_loss`,
`Composition_requires_explicit_repair_after_ambiguous_reset`,
`Composition_keeps_a_real_job_tombstone_and_sealed_output`,
`Composition_recovers_after_replacement_dies_during_startup`, plus
`Private_host_ignores_the_transitional_idle_watchdog`).

**The gates were hiding a test defect, not platform behaviour.** Both test
launchers hard-coded `WindowsPrivateHostProcessLauncher`, and
`GatedContainmentLauncher`'s process wrapper implemented only
`IPrivateHostLaunchedProcess` — so on Unix it erased
`IUnixWorkerContainmentAuthority`, which `PrivateHostAttemptFactory` recovers by
`as`-cast, and startup died with a bounded diagnostic naming nothing. All six
pass on macOS and on Linux once that is fixed.

`Windows_composition_recovers_a_real_host_on_the_same_public_connection` stays
gated deliberately: the Unix twin already covers it and asserts more.

**Cost this created, recorded for a decision — see `.agents/machines.md`.** The
Linux assembly went ~112 s → ~195 s and
`Composition_seals_a_real_background_job_artifact_for_handle_recovery` now fails
~2 runs in 3 *inside the full assembly* while passing 3/3 alone and 15/15 in its
own class. The six new identities are not the direct cause — they pass together
— but they widened the r6x-5 residual window. Better coverage against a flakier
4-CPU assembly is an owner trade-off, not something to patch away silently.

**G8 — Linux leg RUN 2026-07-26 at `3fdbbff`; one failure, now `r6x-5`.**
Architecture 73/73, Guardian **495/496**, server 2,044/2,044, Pester 141 with 2
expected skips, complete stdio handshake. Evidence in `.agents/machines.md`
under "R6 acceptance battery on x64 Linux `magneto`".
The single failure is
`Unix_composition_recovers_real_host_and_descendants_on_the_same_public_connection`
— the readiness poll is budgeted in **attempts** rather than wall-clock time and
exhausted under full-suite saturation on 4 CPUs. It passes 3/3 in isolation on
the same host, so the Linux recovery path itself is proven; the defect is in the
test's observation budget, and the same shape sits at eleven sites across seven
composition identities. See `.agents/review/findings/r6x-5.md`. **Hosted
`ubuntu-latest` runners have the same core count as `magneto`, so this is a
latent CI flake in the acceptance suite, not a `magneto` quirk.**
`r6x-5` is now **fixed — two defects**, both fixed budgets sized against a fast
Mac: the attempt-counted polls, and (found by verifying that fix) the class's
30 s deadline, against which `Composition_isolates_one_alias_worker_crash_from_a_second_alias`
failed 3/3. Each previously failing identity now passes.

**The Linux leg is now CLOSED.** After a third fix — the owner's decision to
serialize the guardian assembly (`maxParallelThreads: 1`, cap value measured
across 1/2/3 threads) — the complete battery is green on x64 Linux for the first
time on this branch: architecture 73/73, Guardian **496/496**, server
2,044/2,044, Pester 141 with 2 expected skips, complete stdio handshake, all
exit codes 0.

**G8 IS CLOSED (2026-07-26).** Windows re-ran at `e5f67a9`: architecture 73/73,
**Guardian 496/496**, Pester 142 with one expected skip, complete stdio
handshake, and server 2,044 with **exactly the 18 pre-existing failures already
recorded** (the ordinary-account cert/DPAPI/mTLS class plus the parked
`Route_pwsh_bypasses_detection_as_consent`) — same count, same identities, and
no server product code changed since the last Windows baseline. That run is also
the first real exercise of the five Windows-only composition identities under
`r6x-5`'s new deadlines, since they return vacuously elsewhere (F1); all pass.

All three platforms now stand where the matrix requires: macOS green, x64 Linux
green, Windows at its recorded baseline.

**G9 — CLOSED 2026-07-26.** The matrix rows B3 and B4's first clause demanded
template bootstrap the lazy-load amendment had already removed. Owner approved
the plan edit on 2026-07-26; `.agents/plans/mcp-resilience.md`
`## Acceptance matrix` now strikes the B3 line and restates B4 as the empty-
runspace baseline. No implementation work remains.

**G10 — CLOSED 2026-07-26.** The original three-barrier claim was wrong.
Re-searching by *mechanism* rather than by name showed foreground-busy was
already covered and G7 made the initialize identity cross-platform. The one
true gap was descriptor bootstrap.

- **foreground-busy was covered all along.** `Composition_never_replays_a_real_effect_when_the_worker_dies`
  kills the worker from inside a live foreground `ptk_invoke`, cross-platform,
  and Windows adds three more kill points via `RealDispatchBarrier`.
- **initialize is cross-platform real-process coverage after G7.**
  `Composition_recovers_after_replacement_dies_during_startup` now receives the
  platform launcher instead of hard-coding Windows.
- **bootstrap is now covered without a timing race.**
  `Hard_kill_during_private_host_descriptor_bootstrap_closes_both_channels`
  launches the existing disposable resilience fixture through the real platform
  launcher. The fixture invokes production `PrivateHostBootstrapCapture` and
  `PrivateHostBootstrapNative`, owns the first inherited handle, writes an exact
  barrier marker, and blocks before returning it. The test hard-kills the process
  at that barrier, observes real launcher containment, event EOF, and a failed
  request write. Mutating the barrier fact from `first_handle_owned=true` to
  false prevented the test from reaching the kill and failed at the exact marker
  assertion; restoration passed.

## Out of scope for this audit

- **The 38 required mutation proofs** (`.agents/plans/mcp-resilience.md`
  `## Required mutation proofs`). No map exists from mutation number to the guard
  it should redden. This static audit neither certifies nor reopens that
  implementation-era gate; each corrective gap slice records its own required
  mutation proof. F3 came out of this audit only because mutation #38 shares
  A3's missing symbol.
- **Re-running any suite.** This pass read tests; it did not execute them.
- **Whether each covering test is non-vacuous.** Rows record that a guard exists,
  not that reverting the behaviour reddens it. `r6x-2` #3 is the standing
  reminder that a green suite is not a correct fix.
