# Retained audit administration and receiver contract

## Current runtime boundary

The production `PtkMcpServer` does not initialize local audit storage and does
not run an OTLP audit producer. Ordinary execution has no journal or collector
dependency, and `ptk_state` reports audit disabled.

`PTK_AUDIT_ROOT` and `PTK_AUDIT_EXPORT_CONFIG` do not enable producer behavior
in the ordinary runtime. The former anchored-startup instructions and
`ptk.export-config/2` example are intentionally not reproduced here.

This repository still contains:

- legacy local journal and exact-script evidence types needed to read and
  disposition existing stores;
- the separate `PtkAuditAdmin` executable for that legacy administration; and
- the standalone `PtkSiemReceiver` OTLP/HTTP wire and acknowledgment contract.

`PtkAuditAdmin` is excluded from the runtime package. `PtkSiemReceiver` is a
separate application, not a service enabled by `PtkMcpServer`.

## Legacy local evidence administration

Legacy stores contain strict `ptk.audit/1` or `ptk.audit/2` core JSONL records
plus owner-only exact-script evidence. Core records reference script evidence
by opaque ID and SHA-256 digest; the exact script bytes are held separately and
may contain credentials, tokens, or customer data.

Point `PtkAuditAdmin` at an existing legacy root with `PTK_AUDIT_ROOT`. Keep the
target producer stopped while administering its store.

```text
PtkAuditAdmin evidence read --id <evidence-uuid>
PtkAuditAdmin evidence export --id <evidence-uuid> --output <absolute-path>
```

`read` writes the exact sensitive bytes to stdout. `export` exclusively creates
a new file in an already owner-only protected directory and refuses an existing
path, symlink, or reparse point. Protect stdout, shell history, redirections,
backups, and exported files as sensitive evidence.

Evidence-access failures use closed detail codes:

| Detail code | Meaning |
| --- | --- |
| `evidence.id_invalid` | The requested evidence ID was not canonical UUIDv4. |
| `evidence.path_invalid` | The export destination was not a valid absolute path. |
| `evidence.absent` | The exact protected evidence object was absent. |
| `evidence.storage_failed` | Evidence or destination storage failed without a narrower classification. |
| `evidence.control_invalid` | Evidence control metadata or protected identity was invalid. |
| `evidence.destination_refused` | OS protection or access control refused the destination. |
| `evidence.destination_exists` | The exclusive export destination already existed. |
| `operation.failed_before_disclosure` | A read failed before destination writing began. |
| `operation.disclosure_unknown` | A destination write began but did not return. |
| `operation.flush_failed_after_disclosure` | The complete write returned but destination flush failed. |
| `audit.outcome_failed_after_disclosure` | Bytes were written and flushed before the legacy terminal record failed. |
| `audit.outcome_failed_after_publish` | A protected export was published before the legacy terminal record failed. |

These stores provide legacy evidence, not hostile same-user isolation. Any
process allowed to execute arbitrary commands as the store owner can also
invoke an accessible administration binary. Use a separate operator identity
or OS application-control boundary when stronger separation is required.

## Legacy checkpoint disposition

A retained legacy exporter can leave one permanent partial/data/protocol block.
With its producer stopped, an operator can either attest a separately verified
durable receipt or explicitly acknowledge an evidence gap:

```text
PtkAuditAdmin disposition --boot-id <uuid> --event-id <uuid> \
  --verified-receipt-digest <lowercase-sha256>

PtkAuditAdmin disposition --boot-id <uuid> --event-id <uuid> \
  --acknowledged-gap-reason <machine-code>
```

The command takes the target boot's exclusive checkpoint lease, resolves the
exact blocked record, persists an idempotent proof-bound disposition intent,
and only then advances that checkpoint. The receipt digest is an operator
attestation; `PtkAuditAdmin` does not query or independently verify a SIEM.
Configuration or authentication failures require correction rather than
disposition.

The decision-log producer evidence at `.agents/decisions.md:312` is known stale
under the existing owner hold. Do not use it to restore the retired producer,
and do not edit that held record as part of runtime documentation maintenance.

## Standalone receiver wire and acknowledgment contract

`siem/PtkSiemReceiver` retains the binary-compatible OTLP logs subset in
`siem/PtkSiemReceiver/Protos/audit_otlp.proto`. Its ingest endpoint is
`POST /v1/logs` over TLS 1.2 or 1.3 with a required client certificate. Request
and response media type is `application/x-protobuf`.

The response contract is:

| Result | HTTP response |
| --- | --- |
| Durable acceptance or an identical already-committed duplicate | `200` with a serialized empty `ExportLogsServiceResponse`; `partial_success` is unset. |
| Permanent validation or protocol refusal | `400` with protobuf `google.rpc.Status`. |
| Transient storage failure, backpressure, or saturated admission | `503` with protobuf `google.rpc.Status` and `Retry-After: 1`. |

Success is written only after the receiver's commit operation reports
acceptance. Delivery is therefore at least once: a client may retry after a
lost response, and the receiver must accept an identical duplicate while
rejecting the same event identity with different content.

The receiver strictly validates the retained audit envelope and projected
attributes, bounds request bodies and concurrent admission, and persists raw
request bytes, event/quarantine state, and receiver custody records in SQLite.
Its deployment status and storage warning remain in
[`siem/PtkSiemReceiver/README.md`](../siem/PtkSiemReceiver/README.md). This
receiver contract is retained for compatibility and future migration work; it
does not imply that the current PTK runtime emits these requests.
