# PtkSiemReceiver operator guide

PtkSiemReceiver is PTK's standalone OTLP/HTTP audit destination for sites that
do not already have a SIEM. It accepts PTK audit records at `/v1/logs`, stores
the exact record evidence in SQLite, verifies the producer hash chain, and
serves a separate operator API and dashboard.

Export is additive to PTK's local journal. PTK executes and journals locally
even while this receiver is unavailable; a background pump retries delivery
at least once and advances `<audit-root>/export-cursor.json` only after the
destination accepts a batch.

## Deployment boundary

An anchored deployment uses a receiver host, VM, or equivalently isolated
machine that is separate from every PTK producer and is administered under a
different OS principal. The receiver process runs as a dedicated, non-login
service account. A second account on the producer host is useful for
development, but it is not an anchored deployment: compromise of that host can
reach both sides of the custody boundary.

Install the executable in a root-owned, non-writable program directory. Give
the receiver identity access only to its configuration/TLS directory, SQLite
data directory, custody-witness directory, and optional anchor directory.
SQLite, witness, and anchor directories must be independent; neither witness
nor anchor may be the same as or below the SQLite data directory, and the
anchor may not be the same as or below the witness directory (or vice versa).
An anchor on off-host or write-once storage is the recommended default.

Release packaging and native installer/service registration belong to S8. For
a source-built installation, publish from the trusted revision into a staging
directory and copy the resulting payload into the program directory:

```text
dotnet publish siem/PtkSiemReceiver/PtkSiemReceiver.csproj -c Release -r <RID> --self-contained true -o <staging-directory>
```

Use the RID for the receiver host, such as `linux-x64`, `linux-arm64`,
`osx-x64`, `osx-arm64`, `win-x64`, or `win-arm64`.

### Linux

Create a system account with no interactive shell (for example `ptk-siem`)
and run the receiver as that account under the site's service manager. A
typical layout is:

```text
/opt/ptk-siem/                  root-owned program files
/etc/ptk-siem/                 receiver-owned configuration and TLS files
/var/lib/ptk-siem/             receiver-owned SQLite directory
/var/lib/ptk-siem-witness/     receiver-owned witness directory
/mnt/ptk-siem-anchor/          receiver-owned off-host/write-once file drop
```

Set each protected directory to exactly `0700` and every protected file to
exactly `0600`, owned by the service account. A systemd unit should set
`User=ptk-siem`, set `PTK_SIEM_CONFIG` to the absolute configuration path,
start the published executable directly, and grant write access only to the
three state directories. Use `NoNewPrivileges=true` and the site's normal
service hardening where it does not prevent access to those paths.

### macOS

Create a dedicated hidden/non-login account through MDM, Directory Utility,
or the site's account-management tooling. Keep program files root-owned and
use independent receiver-owned directories such as:

```text
/Library/Application Support/PTKSiem/config/
/Library/Application Support/PTKSiem/data/
/Library/Application Support/PTKSiem/witness/
/Volumes/PTKSiemAnchor/
```

Run the executable with a LaunchDaemon whose `UserName` is that account and
whose environment sets `PTK_SIEM_CONFIG`. Protected directories and files use
exact POSIX modes `0700` and `0600`. Remove extended ACLs during provisioning;
the receiver rejects any macOS extended ACL rather than trying to repair it.
Do not configure a path through `/var` or another symlink alias—use its real
lexical path (for example `/private/var/...`) if that location is required.

### Windows

Use a dedicated local service account, domain service account, gMSA, or
service virtual account that is used for no other workload. Run the published
console executable under that identity using the site's service wrapper or
startup scheduler until S8 supplies native service packaging. Keep program
files under an administrator-owned directory and use independent state roots,
for example:

```text
C:\ProgramData\PtkSiem\config
C:\ProgramData\PtkSiem\data
D:\PtkSiemWitness
E:\PtkSiemAnchor
```

The receiver identity must own every protected directory and file. Disable
inheritance and give the object exactly one non-inherited allow ACE: that
identity with `FullControl`. Do not add Administrators, SYSTEM, backup agents,
deny ACEs, or inherited ACEs to a protected object; perform backups through a
separately protected destination while executing the SQLite backup command as
the receiver identity. Provision these ACLs explicitly for the config, every
PEM file, and each state directory before startup.

### Protected-path startup gate

Startup completes the filesystem gate before either listener binds. The
configuration file, every referenced TLS file, the SQLite database and its
`-wal`/`-shm` sidecars, and the immediate directories containing those files
must satisfy the platform policy above. The witness and anchor directories
themselves must also satisfy that policy; their parents are checked for path
redirection, but not for ownership, mode, or ACL policy. Provision those
parents so no other principal can rename or delete the protected directories.
All configured paths must be absolute. Every existing lexical path component
is checked without following it; symlinks, junctions, mount-point reparse
points, and other redirects are refused. Existing insecure objects covered by
the gate fail closed and are never repaired. The receiver creates missing
SQLite files securely inside an already protected data directory.

These checks protect against another OS principal. They do not protect
against malicious code already running as the receiver service identity.

## Configuration

Set `PTK_SIEM_CONFIG` to one strict UTF-8 JSON file. The file is read once and
frozen at startup; unknown properties, a UTF-8 BOM, relative paths, reused
ingest/operator tokens, and invalid security combinations are rejected.

This complete Linux-shaped example enables token-authenticated PTK export,
mTLS clients, a loopback operator surface, retention, all four alert-rule
types, webhook delivery, independent custody checkpoints, and an off-host
file-drop anchor:

```json
{
  "ingest": {
    "bindAddress": "0.0.0.0",
    "port": 4318,
    "serverCertificatePath": "/etc/ptk-siem/ingest-cert.pem",
    "serverCertificateKeyPath": "/etc/ptk-siem/ingest-key.pem",
    "clientCaBundlePaths": [
      "/etc/ptk-siem/client-ca-current.pem",
      "/etc/ptk-siem/client-ca-next.pem"
    ],
    "revocationCheckMode": "Online",
    "maxRequestBytes": 1048576,
    "maxConcurrentRequests": 64,
    "token": "replace-with-a-random-ingest-secret-of-at-least-16-characters"
  },
  "operator": {
    "bindAddress": "127.0.0.1",
    "port": 9443,
    "token": "replace-with-a-different-random-operator-secret"
  },
  "storage": {
    "sqlitePath": "/var/lib/ptk-siem/events.db",
    "retention": {
      "maxAgeDays": 90,
      "maxTotalBytes": 10737418240
    },
    "custodyWitness": {
      "directoryPath": "/var/lib/ptk-siem-witness",
      "checkpointIntervalSeconds": 60,
      "anchorDirectoryPath": "/mnt/ptk-siem-anchor"
    }
  },
  "alerts": {
    "rules": [
      {
        "name": "completed-execution",
        "type": "event_match",
        "eventType": "execution.completed"
      },
      {
        "name": "producer-chain-break",
        "type": "chain_break"
      },
      {
        "name": "producer-gap",
        "type": "gap_detected"
      },
      {
        "name": "ingest-burst",
        "type": "ingest_rate",
        "threshold": 1000,
        "windowSeconds": 60
      }
    ],
    "webhookUrl": "https://alerts.example.net/ptk"
  }
}
```

Important field rules:

- Ingest always uses HTTPS. `clientCaBundlePaths` is a required, nonempty CA
  bundle list even when PTK uses the bearer token. Multiple bundles allow an
  old/new CA overlap during client-certificate rotation.
- `revocationCheckMode` is exact and explicit: `NoCheck`, `Online`, or
  `Offline`. `Online` requires the receiver host to reach the certificate's
  configured revocation services. There is no silent fallback.
- `maxRequestBytes` defaults to 1 MiB and `maxConcurrentRequests` to 64.
  Excess concurrency receives a retryable `503` rather than an unbounded
  queue. The ingest token is optional but must be at least 16 characters when
  set; use a generated high-entropy secret.
- The operator bind defaults to `127.0.0.1`. A non-loopback bind is rejected
  unless both `httpsCertificatePath` and `httpsCertificateKeyPath` are set.
  The operator and ingest ports must differ, and their tokens must differ.
- `custodyWitness` and `directoryPath` are required. The checkpoint interval
  defaults to 60 seconds and may be 1 through 86,400 seconds.
- `retention` is optional. If present, it must contain at least one positive
  bound. Omitting it means unbounded growth.
- `alerts` is optional. If present, `rules` must be nonempty and each rule's
  fields are exact: `event_match` requires only `eventType`; `ingest_rate`
  requires only `threshold` and `windowSeconds`; `chain_break` and
  `gap_detected` take no extra fields. A webhook must use HTTPS unless it is
  loopback HTTP.

The server certificate must name the address producers use and chain to a CA
trusted by every producer host. PTK's exporter uses normal platform TLS
validation and does not accept a receiver-specific trust bypass.

## Start and connect PTK

Start the receiver under its service identity with `PTK_SIEM_CONFIG` set. A
missing or invalid configuration exits with a sanitized
`siem_receiver_configuration_invalid` code; a failed storage/TLS/witness gate
exits with a sanitized `siem_receiver_startup_failed` code.

Configure each producer with the full ingest URL and the receiver's ingest
token:

```text
PTK_AUDIT_EXPORT_KIND=otlp_http
PTK_AUDIT_EXPORT_ENDPOINT=https://receiver.example.net:4318/v1/logs
PTK_AUDIT_EXPORT_TOKEN=<receiver-ingest-token>
```

Restart the producer supervisor after changing these variables. The exporter
pump normally notices work within two seconds. `ptk_state` reports the
destination, delivered count, pending bytes, retry state, and permanent
refusal/gap warnings separately from local audit health. A healthy cursor
advance proves destination acceptance; it does not gate or retroactively
authorize the PTK operation.

## Operator surface

The operator listener is separate from ingest. Its static dashboard at `/`
contains no evidence and is served without a token; the browser keeps the
operator token in session storage and sends it only in the `Authorization`
header. Every API request requires `Authorization: Bearer <operator-token>`.
Do not put either token in a URL.

Available routes are:

- `GET /api/events` with optional `from`, `to`, `type`, `session`, `boot`, and
  `limit` filters; `GET /api/events/{eventId}` includes chain context.
- `GET /api/chains`, `/api/quarantine`, `/api/gaps`, and `/api/alerts`.
  Alerts accept the optional `state` filter.
- `POST /api/gaps/{gapId}/disposition` with
  `{"disposition":"resolved"}` or `{"disposition":"accepted-loss"}`.
- `POST /api/alerts/{alertId}/transition` with
  `{"state":"acknowledged"}` or `{"state":"closed"}`. The only lifecycle
  is `open` → `acknowledged` → `closed`.
- `GET /api/custody/health` and, only during a detected older-database
  restore, `POST /api/custody/restore`.

Use an SSH/management tunnel to a loopback operator listener when practical.
If remote binding is required, configure a separately protected HTTPS
certificate and restrict the port at the host/network firewall.

## Retention and capacity

Retention runs at startup and every 15 minutes while custody is healthy:

- Age retention removes old events, quarantine attempts, and closed alerts.
  Open/acknowledged alerts, unresolved-gap evidence, pending alert inputs, and
  per-boot chain heads remain live.
- Size retention removes the oldest eligible events and quarantine attempts
  in bounded batches and compacts SQLite. It does not remove alerts merely to
  meet the size target.
- Every purge transaction first appends custody-protected tombstone evidence
  that commits to the removed subjects and producer-chain boundaries.
- Custody receipts, retention tombstones, restore evidence, witness records,
  and chain heads are append-only and never removed by retention.

`maxTotalBytes` is therefore a target for deletable SQLite evidence, not a
hard cap on total storage. Custody and tombstone growth can eventually exceed
it, and witness/anchor file counts also grow without an implemented rotation
policy. Monitor all three storage roots, alert before filesystem exhaustion,
and increase capacity rather than deleting custody history. A custody failure
or restore-pending state pauses ingest, alert evaluation, and retention.

## Custody witness and attestation

The SQLite custody ledger detects row/evidence mutation but cannot by itself
detect replacement of the whole database or truncation of its tail. The
independent witness directory contains immutable, hash-chained custody-head
checkpoints written on the configured cadence and clean shutdown. The
optional anchor file-drop is written first and pins the witness history on
off-host/write-once storage.

Check health with the operator API. Healthy operation requires `healthy=true`
and `restore_pending=false`. Preserve the returned `witness_sequence` and
`witness_hash` off-host when no anchor directory is configured. The manual
attestation cadence must be fixed and no longer than the site's tolerated
undetected-rewrite window; record the sequence, hash, `checked_utc`, receiver
identity, and operator identity in a system the receiver service account
cannot rewrite.

The local witness detects store rewrite/truncation only while the attacker
cannot also forge witness files. Coordinated database+witness rewrite, and
loss of records newer than the latest independently retained checkpoint, are
detectable only through the off-host anchor or manual attestation.

## Online backup

Do not copy a live SQLite database and guess whether its WAL belongs to the
copy. Run SQLite's online backup API as the receiver identity. The `sqlite3`
CLI's `.backup` command uses that API and obtains a transactionally consistent
snapshot while ingest continues:

```text
sqlite3 /var/lib/ptk-siem/events.db ".backup '/protected-backups/ptk-siem/events-YYYYMMDDTHHMMSSZ.db'"
sqlite3 /protected-backups/ptk-siem/events-YYYYMMDDTHHMMSSZ.db "PRAGMA quick_check;"
```

On POSIX, use an owner-only backup directory and a `077` umask so the snapshot
is `0600`. On Windows, run the equivalent
`sqlite3.exe <database> ".backup '<backup>'"` under the receiver identity and
apply a protected one-ACE DACL to the backup. Encrypt backup media according
to the site's evidence policy.

Also back up the frozen configuration and TLS material through the site's
secret-management process. Continuously preserve the independent witness and
anchor histories, but treat them as monotonic evidence—not as files to roll
back with a database snapshot. Record the current custody/witness health tuple
with each backup manifest. Test restores on an isolated host.

## Witness-aware restore

Restoring an older database is an explicit data-loss reconciliation, not a
normal overwrite:

1. Stop the receiver and preserve a rollback copy of the current database,
   `-wal`, and `-shm` files in a protected directory. Do not modify, replace,
   or restore an older copy over the current witness or anchor directories.
2. Put the selected online-backup database at the configured `sqlitePath`.
   Do not reuse the newer database's WAL/SHM sidecars. Restore the exact
   owner-only file protection before startup.
3. Start the receiver. When the database head is older than retained witness
   history, the receiver enters restore-pending state. Queries remain
   available, but mutation is paused and ingest returns retryable `503`.
4. Authenticate to `GET /api/custody/health`. Compare the restored custody
   head with the preserved witness/anchor and the backup manifest. If the
   selected loss is not understood and authorized, stop and restore the
   rollback copy instead.
5. To authorize the known loss, send authenticated `POST
   /api/custody/restore` with an empty body. The receiver appends witnessed
   branch/restore evidence, custody-records the reconciliation, creates
   exactly one open `custody_restore_data_loss` alert, checkpoints the new
   branch, and resumes mutation. A second authorization returns `409`.
6. Verify custody health is healthy, review and disposition the data-loss
   alert through the normal alert API, then monitor producers until export is
   healthy.

PTK retries unaccepted work at least once, and replaying still-retained source
records is idempotent at the receiver. A producer cursor that already advanced
past the lost receiver suffix does not automatically rewind; recovering older
retained producer records is a separate, controlled producer-recovery action.
Do not delete or hand-edit `export-cursor.json` as an ad-hoc restore step.

Never "fix" a restore by replacing a newer witness or anchor with the older
database's contemporaneous copy. That destroys the evidence the reconciliation
protocol is designed to preserve.

## Upgrade and schema migration

This revision's SQLite schema is version 10. Migrations are automatic,
transactional, and forward-only at receiver startup. A database with a schema
newer than the binary is refused (`storage_schema_newer`); binary downgrade is
unsupported.

For every upgrade:

1. Read the release notes and configuration changes, run an online database
   backup, confirm `PRAGMA quick_check`, and preserve the current health tuple.
2. Stop the receiver cleanly so it writes a final checkpoint. Preserve the
   witness and anchor unchanged.
3. Replace only the program payload, keeping the prior payload and database
   backup in protected rollback storage. Recheck file ownership/permissions.
4. Start the new binary, verify `GET /api/custody/health`, ingest one test
   event, and confirm it through `/api/events` and producer export health.

If migration has occurred, rolling back only the executable will not work.
Move forward with a corrected binary or restore the pre-upgrade database using
the witness-aware procedure above; the latter may require explicit data-loss
authorization if newer custody exists.

## Threat model and acceptance mapping

Protected assets are the exact audit record bodies, producer and receiver hash
chains, quarantine/gap/alert state, retention/restore evidence, credentials,
TLS private keys, and operator availability. Trust boundaries are the producer
host, TLS ingest connection, receiver service identity, operator connection,
SQLite root, independent witness root, and optional off-host anchor.

| Acceptance row | Control | Residual risk / operator obligation |
|---|---|---|
| 1 — threat model and separate identity | A different host or equivalently isolated machine and dedicated receiver principal move evidence outside the producer's administration boundary. | Same-host installs are non-anchoring. Host administrators and code running as the receiver identity can still alter process/binary state; use separate administration and OS hardening. |
| 6 — mTLS or equivalent | Ingest is HTTPS. Clients authenticate with a validated client certificate (custom CA roots, client-auth EKU, validity, explicit revocation policy) or a high-entropy bearer token. PTK uses the token mode. | Theft of a client key/token permits ingest attempts until rotation. TLS does not prevent denial of service; restrict the port, rate-limit upstream, rotate secrets, and monitor quarantine/alerts. |
| 7 — receiver storage protection | Startup fails closed on wrong ownership, modes/DACLs, links/reparse points, path replacement, insecure TLS/config/database sidecars, or insecure witness/anchor roots. Custody hashes reveal evidence mutation. | The service identity must write the data it protects. An attacker with that identity can read secrets and coordinate changes; witness/anchor separation limits, but does not erase, that risk. |
| 8 — retention and read authorization | API evidence requires a distinct operator bearer token; plaintext operator HTTP is loopback-only. Retention writes verifiable tombstones and preserves live triage/chain state. | The static dashboard shell is public on the operator port. Retention is not a hard capacity bound because append-only evidence grows; monitor storage and protect the operator token/browser/session. |
| 10 — upgrade, backup, recovery | SQLite online backup, forward-only migrations, independent witness history, restore-pending mutation pause, and authenticated restore reconciliation prevent a silent rollback. | Backup confidentiality/integrity is an operator responsibility. An authorized restore records but cannot recover data that no producer or backup retains. Downgrade is unsupported. |
| 11 — network patch burden | The network surfaces and runtime dependencies are small and inventoried below; request size/concurrency are bounded and the dashboard vendors no script. | This is still an Internet-capable TLS/HTTP service with a native SQLite library. Firewall it, minimize exposure, track advisories, and patch on a defined cadence. |

Further limits:

- A valid `200` is emitted only after the SQLite transaction commits under
  WAL + `synchronous=FULL`; storage/backpressure failures return retryable
  `503`, and permanent bad records are quarantined and refused.
- The receiver validates producer event IDs, exact bodies, projected OTLP
  attributes, sequence, and hash-chain continuity. It cannot prove that a
  fully compromised producer generated truthful source events.
- Request-size and concurrency limits bound receiver work per admitted
  request, not aggregate network floods or disk exhaustion.
- Without an off-host anchor/manual attestation, a receiver-identity attacker
  who can rewrite both database and witness can erase history. Records after
  the last independently retained checkpoint remain an unwitnessed suffix.
- Destruction of the database, witness, anchor, and every backup leaves no
  local mechanism from which to reconstruct custody.

## Network and dependency inventory

Inbound surfaces:

- Ingest TCP listener: configurable address/port, HTTPS only, `POST /v1/logs`,
  OTLP/HTTP JSON or protobuf, mTLS or bearer-token authentication.
- Operator TCP listener: separate address/port, loopback HTTP by default or
  HTTPS when remote; dashboard plus the authenticated routes listed above.

Optional outbound surfaces are HTTPS alert-webhook delivery, certificate
revocation lookups when `Online` is selected, and filesystem traffic to an
off-host anchor mount. The receiver has no required cloud service, analytics,
or third-party browser script.

Direct package inventory at this revision:

| Component | Version | Role |
|---|---:|---|
| .NET / ASP.NET Core | 10.0 target | Host, Kestrel TLS/HTTP, cryptography, JSON |
| Google.Protobuf | 3.35.1 | OTLP protobuf parsing |
| Grpc.Tools | 2.82.0 | Build-time protobuf code generation; not a runtime dependency |
| Microsoft.Data.Sqlite / Core | 10.0.10 | Managed SQLite provider |
| SQLitePCLRaw bundle/core/provider/native `e_sqlite3` | 2.1.12 | Native SQLite engine and provider |

At least monthly, and immediately after a relevant .NET, ASP.NET Core,
SQLite, protobuf, TLS, or OS advisory, inventory the deployed versions and run:

```text
dotnet list siem/PtkSiem.slnx package --vulnerable --include-transitive
dotnet test siem/PtkSiem.slnx
```

Apply Critical network/crypto fixes on the site's emergency cadence and all
other applicable security updates in the next normal maintenance window.
Rebuild from the patched trusted revision, perform the upgrade procedure, and
keep the ingest/operator ports limited to the networks that need them. A
successful command exit with any listed vulnerable package is a failed
dependency check.
