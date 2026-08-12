using System.Globalization;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Ingest;
using PtkSiemReceiver.Storage;
using PtkMcpServer.Audit.OtlpWire;

namespace PtkSiemReceiver.Tests;

/// <summary>
/// rbc-11: retention options were parsed but never enforced, so an unattended
/// receiver grew without bound and its README warned against deploying it.
/// These guards pin enforcement AND the boundary that makes it safe — the
/// append-only custody ledger is never swept.
/// </summary>
public sealed class RetentionEnforcementTests
{
    private static readonly IngestReceiptContext Receipt = new(
        new DateTimeOffset(2026, 7, 15, 16, 30, 45, TimeSpan.Zero),
        new string('a', 64),
        "127.0.0.1:4318");

    [Fact]
    public async Task Age_retention_removes_old_events_and_keeps_custody_receipts()
    {
        using var database = new TestDatabase();
        using var store = SqliteIngestStore.Open(database.Path);
        await CommitChainAsync(store, count: 4);

        Assert.Equal(4L, Count(database.Path, "events"));
        var custodyBefore = Count(database.Path, "custody");
        Assert.Equal(4L, custodyBefore);

        // Every record was received "now"; sweeping with a 30-day bound a year
        // later ages all of them out.
        var outcome = await store.EnforceRetentionAsync(
            maximumAgeDays: 30,
            maximumTotalBytes: null,
            utcNow: Receipt.ReceivedUtc.AddYears(1),
            CancellationToken.None);

        Assert.True(outcome.EventsRemoved >= 3);
        // The custody ledger is the append-only witness of what was received:
        // retention must never erase the evidence it exists to protect.
        Assert.Equal(custodyBefore, Count(database.Path, "custody"));
        // The chain head survives so a later record still validates against
        // its predecessor's hash.
        Assert.Equal(1L, Count(database.Path, "events"));
        Assert.Equal(1L, Count(database.Path, "chains"));
    }

    [Fact]
    public async Task Age_retention_keeps_records_inside_the_window()
    {
        using var database = new TestDatabase();
        using var store = SqliteIngestStore.Open(database.Path);
        await CommitChainAsync(store, count: 3);

        var outcome = await store.EnforceRetentionAsync(
            maximumAgeDays: 30,
            maximumTotalBytes: null,
            utcNow: Receipt.ReceivedUtc.AddDays(1),
            CancellationToken.None);

        Assert.Equal(0, outcome.EventsRemoved);
        Assert.Equal(3L, Count(database.Path, "events"));
    }

    [Fact]
    public async Task Unconfigured_retention_removes_nothing()
    {
        using var database = new TestDatabase();
        using var store = SqliteIngestStore.Open(database.Path);
        await CommitChainAsync(store, count: 3);

        var outcome = await store.EnforceRetentionAsync(
            maximumAgeDays: null,
            maximumTotalBytes: null,
            utcNow: Receipt.ReceivedUtc.AddYears(5),
            CancellationToken.None);

        Assert.Equal(0, outcome.EventsRemoved);
        Assert.Equal(0, outcome.QuarantineRemoved);
        Assert.Equal(3L, Count(database.Path, "events"));
    }

    [Fact]
    public async Task Size_retention_bounds_the_store_and_reports_its_size()
    {
        using var database = new TestDatabase();
        using var store = SqliteIngestStore.Open(database.Path);
        await CommitChainAsync(store, count: 24);

        // Baseline through the same PRAGMA-derived measure the sweep uses:
        // under WAL the main file's length is not the database size.
        var baseline = await store.EnforceRetentionAsync(
            maximumAgeDays: null,
            maximumTotalBytes: long.MaxValue,
            utcNow: Receipt.ReceivedUtc,
            CancellationToken.None);
        Assert.Equal(0, baseline.EventsRemoved);
        var beforeBytes = baseline.DatabaseBytes;

        // A bound below the current size must actually shrink the database:
        // deleting rows without reclaiming pages would never converge.
        var outcome = await store.EnforceRetentionAsync(
            maximumAgeDays: null,
            maximumTotalBytes: beforeBytes / 2,
            utcNow: Receipt.ReceivedUtc,
            CancellationToken.None);

        Assert.True(
            outcome.EventsRemoved > 0,
            "a size-bounded sweep removed nothing");
        Assert.True(
            outcome.DatabaseBytes < beforeBytes,
            $"database did not shrink: {outcome.DatabaseBytes} >= {beforeBytes}");
        Assert.Equal(Count(database.Path, "custody"), 24L);
    }

    [Fact]
    public async Task An_age_purge_that_frees_enough_space_deletes_no_fresh_records()
    {
        // cr3-4: the size loop measured raw page_count, which still counts
        // pages the age purge freed, so a combined sweep deleted fresh
        // in-window records that did not need to go. The first version of
        // this guard passed against the defect because its size bound was
        // never exceeded (verification reopen) — it now uses mixed-age data
        // and a bound that only the LIVE measure satisfies.
        using var database = new TestDatabase();
        using var store = SqliteIngestStore.Open(database.Path);

        // 96 old records, then 6 inside the retention window. The old bulk
        // must dominate the store's fixed page overhead (schema v2 added
        // empty tables and indexes), or the half-size bound sits inside the
        // overhead and the discrimination below turns on page arithmetic.
        var old = Receipt with { ReceivedUtc = Receipt.ReceivedUtc.AddYears(-1) };
        await CommitChainAsync(store, count: 96, receipt: old);
        await CommitChainAsync(
            store,
            count: 6,
            receipt: Receipt,
            startSequence: 97,
            previousHash: LastHash);

        var beforeSweep = await store.EnforceRetentionAsync(
            maximumAgeDays: null,
            maximumTotalBytes: long.MaxValue,
            utcNow: Receipt.ReceivedUtc,
            CancellationToken.None);

        // A bound at the pre-purge size: the raw page count still exceeds it
        // after the age purge (pages are freed, not removed), so the defective
        // measure keeps deleting; the live measure stops.
        var outcome = await store.EnforceRetentionAsync(
            maximumAgeDays: 30,
            maximumTotalBytes: beforeSweep.DatabaseBytes / 2,
            utcNow: Receipt.ReceivedUtc,
            CancellationToken.None);

        // The six in-window records must survive: the age purge alone freed
        // more than enough room.
        Assert.Equal(6L, Count(database.Path, "events"));
        Assert.True(
            outcome.DatabaseBytes <= beforeSweep.DatabaseBytes,
            "the sweep reported more live bytes than the baseline");
    }

    [Fact]
    public void Retention_releases_the_writer_gate_between_pruning_chunks()
    {
        // cr3-3: the sweep held the ingest writer semaphore across its whole
        // size loop with a full VACUUM per batch, so live commits stalled
        // behind retention. A timing test could not prove this (the first
        // version passed against the defect too, because a small sweep is
        // instant — verification reopen), so the property is pinned
        // structurally: the size loop must take AND release the gate inside
        // the loop, and compaction must not run per batch.
        var source = File.ReadAllText(FindRepositoryFile(
            "siem/PtkSiemReceiver/Storage/SqliteIngestStore.cs"));
        var start = source.IndexOf(
            "internal async Task<SiemRetentionOutcome> EnforceRetentionAsync",
            StringComparison.Ordinal);
        Assert.True(start > 0, "EnforceRetentionAsync was not found");
        var loopStart = source.IndexOf(
            "while (!cancellationToken.IsCancellationRequested)",
            start,
            StringComparison.Ordinal);
        Assert.True(loopStart > 0, "the bounded pruning loop was not found");
        var loopEnd = source.IndexOf("if (pruned)", loopStart, StringComparison.Ordinal);
        Assert.True(loopEnd > loopStart, "the post-pruning compaction was not found");

        var loopBody = source[loopStart..loopEnd];
        Assert.Contains("_writerGate.WaitAsync", loopBody, StringComparison.Ordinal);
        Assert.Contains("_writerGate.Release();", loopBody, StringComparison.Ordinal);
        // Compaction belongs after pruning, never inside the chunk loop.
        Assert.DoesNotContain("Vacuum();", loopBody, StringComparison.Ordinal);
        Assert.Contains(
            "Vacuum();",
            source[loopEnd..],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ingest_completes_while_a_retention_sweep_runs()
    {
        // The behavioral companion to the structural pin above: a commit
        // issued during a sweep completes rather than deadlocking.
        using var database = new TestDatabase();
        using var store = SqliteIngestStore.Open(database.Path);
        await CommitChainAsync(store, count: 40);

        var baseline = await store.EnforceRetentionAsync(
            maximumAgeDays: null,
            maximumTotalBytes: long.MaxValue,
            utcNow: Receipt.ReceivedUtc,
            CancellationToken.None);

        var sweep = store.EnforceRetentionAsync(
            maximumAgeDays: null,
            maximumTotalBytes: baseline.DatabaseBytes / 2,
            utcNow: Receipt.ReceivedUtc,
            CancellationToken.None);

        var ingest = store.CommitAsync(
            Validate(OtlpTestRequest.Create(
                eventId: "018f6a78-4c20-7a11-8a34-1234567890ff",
                supervisorBootId: "3b7576e5-7763-4aa8-9741-3bc1d6a7e15d",
                sequence: 1)),
            Receipt,
            CancellationToken.None);

        var completed = await Task.WhenAny(
            ingest,
            Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(ingest, completed);
        Assert.Equal(IngestCommitResultKind.Accepted, (await ingest).Kind);
        await sweep;
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(relativePath);
    }

    [Fact]
    public async Task The_retention_service_sweeps_on_demand_and_survives_a_failure()
    {
        using var database = new TestDatabase();
        using var store = SqliteIngestStore.Open(database.Path);
        await CommitChainAsync(store, count: 4);

        var options = new SiemReceiverOptions(
            System.Net.IPAddress.Loopback,
            0,
            "/unused/server.pem",
            "/unused/server.key",
            ["/unused/ca.pem"],
            System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            maxRequestBytes: 1024 * 1024,
            maxConcurrentRequests: 4,
            operatorBindAddress: System.Net.IPAddress.Loopback,
            operatorPort: 9,
            operatorToken: new string('t', 32),
            operatorHttpsCertificatePath: null,
            operatorHttpsCertificateKeyPath: null,
            sqlitePath: database.Path,
            retentionMaxAgeDays: 30,
            retentionMaxTotalBytes: null);
        var service = new RetentionService(
            options,
            store,
            NullLogger<RetentionService>.Instance,
            interval: TimeSpan.FromMinutes(15),
            timeProvider: new FixedTimeProvider(Receipt.ReceivedUtc.AddYears(1)));

        var outcome = await service.SweepOnceAsync(CancellationToken.None);
        Assert.NotNull(outcome);
        Assert.True(outcome!.EventsRemoved > 0);

        // A sweep against a disposed store must not throw into the host:
        // retention housekeeping never takes ingest down.
        store.Dispose();
        var afterFailure = await service.SweepOnceAsync(CancellationToken.None);
        Assert.Null(afterFailure);
    }

    [Fact]
    public async Task Retention_never_deletes_subjects_of_pending_alert_work()
    {
        // cr8-2: a committed work item promises an evaluation over durable
        // inputs; sweeping its subject row away would silently suppress the
        // alert while the cursor advances past it.
        using var database = new TestDatabase();
        var hash = new string('a', 64);
        using var store = SqliteIngestStore.Open(
            database.Path, alertRuleConfigHash: hash);
        await CommitChainAsync(store, count: 3);
        var outOfPlace = Validate(OtlpTestRequest.Create(eventType: "tool.rejected"));
        var rejected = await store.CommitAsync(outOfPlace, Receipt, CancellationToken.None);
        Assert.Equal(IngestCommitResultKind.PermanentFailure, rejected.Kind);
        Assert.Equal(4L, Count(database.Path, "alert_queue"));

        // Everything is a year stale, but every subject is still pending.
        _ = await store.EnforceRetentionAsync(
            maximumAgeDays: 30,
            maximumTotalBytes: null,
            utcNow: Receipt.ReceivedUtc.AddYears(1),
            CancellationToken.None);
        Assert.Equal(3L, Count(database.Path, "events"));
        Assert.Equal(1L, Count(database.Path, "quarantine"));

        // Drained, the same sweep reclaims all of it (head excepted).
        IReadOnlyList<PtkSiemReceiver.Configuration.AlertRule> rules =
            [new("r", "chain_break", null, null, null)];
        while (await store.EvaluateNextAlertWorkItemAsync(
                   rules, hash, Receipt.ReceivedUtc, CancellationToken.None) is not null)
        {
        }

        _ = await store.EnforceRetentionAsync(
            maximumAgeDays: 30,
            maximumTotalBytes: null,
            utcNow: Receipt.ReceivedUtc.AddYears(1),
            CancellationToken.None);
        Assert.Equal(1L, Count(database.Path, "events"));
        Assert.Equal(0L, Count(database.Path, "quarantine"));
    }

    [Fact]
    public async Task An_unresolved_gaps_opening_attempt_survives_retention()
    {
        // cr8-4: the rejected record that opened a gap is the gap's proof —
        // its claimed hashes and raw bytes must outlive every sweep until
        // the gap resumes.
        using var database = new TestDatabase();
        using var store = SqliteIngestStore.Open(database.Path);
        await CommitChainAsync(store, count: 1);
        var gapped = Validate(OtlpTestRequest.Create(
            eventId: "018f6a78-4c20-7a11-8a34-1234567890f3",
            sequence: 3,
            previousEventHash: LastHash));
        Assert.Equal(
            IngestCommitResultKind.PermanentFailure,
            (await store.CommitAsync(gapped, Receipt, default)).Kind);
        Assert.Equal(1L, Count(database.Path, "quarantine"));
        Assert.Equal(1L, Count(database.Path, "gaps"));

        _ = await store.EnforceRetentionAsync(
            maximumAgeDays: 30,
            maximumTotalBytes: null,
            utcNow: Receipt.ReceivedUtc.AddYears(1),
            CancellationToken.None);
        Assert.Equal(1L, Count(database.Path, "quarantine"));

        // Resumed (post-gap anchor stored, operator disposition), the
        // evidence becomes ordinarily sweepable.
        Assert.Equal(
            IngestCommitResultKind.Accepted,
            (await store.CommitAsync(gapped, Receipt, default)).Kind);
        Assert.Equal(
            GapDispositionOutcome.Resumed,
            await store.DispositionGapAsync(1, "accepted-loss", Receipt, default));
        _ = await store.EnforceRetentionAsync(
            maximumAgeDays: 30,
            maximumTotalBytes: null,
            utcNow: Receipt.ReceivedUtc.AddYears(1),
            CancellationToken.None);
        Assert.Equal(0L, Count(database.Path, "quarantine"));
    }

    [Fact]
    public async Task A_migrated_pre_v4_gap_regains_its_opening_attempt_link()
    {
        // cr8-4 verification reopen: a v3 store's existing unresolved gap
        // migrated with opening_attempt_id NULL, so its quarantine evidence
        // was still sweepable. The v5 backfill relinks it.
        using var database = new TestDatabase();
        using (var store = SqliteIngestStore.Open(database.Path))
        {
            await CommitChainAsync(store, count: 1);
            var gapped = Validate(OtlpTestRequest.Create(
                eventId: "018f6a78-4c20-7a11-8a34-1234567890f4",
                sequence: 3,
                previousEventHash: LastHash));
            Assert.Equal(
                IngestCommitResultKind.PermanentFailure,
                (await store.CommitAsync(gapped, Receipt, default)).Kind);
        }

        // Reconstruct the faithful v3 shape: no opening-attempt column,
        // versions rolled back.
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder
                   {
                       DataSource = database.Path,
                       Mode = SqliteOpenMode.ReadWrite,
                       Pooling = false,
                   }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE gaps DROP COLUMN opening_attempt_id;
                UPDATE meta SET value = '3' WHERE key = 'schema_version';
                PRAGMA user_version=3;
                """;
            command.ExecuteNonQuery();
        }

        using (var reopened = SqliteIngestStore.Open(database.Path))
        {
            Assert.Equal(
                1L,
                Scalar<long>(
                    database.Path,
                    "SELECT opening_attempt_id FROM gaps WHERE state = 'open';"));
            _ = await reopened.EnforceRetentionAsync(
                maximumAgeDays: 30,
                maximumTotalBytes: null,
                utcNow: Receipt.ReceivedUtc.AddYears(1),
                CancellationToken.None);
            Assert.Equal(1L, Count(database.Path, "quarantine"));
        }
    }

    [Fact]
    public async Task Spent_queue_rows_and_aged_closed_alerts_are_reclaimed()
    {
        // cr8-5: evaluated queue rows are deleted with the cursor advance,
        // and closed alerts age out with a custody-chained tombstone; open
        // alerts of the same age are live triage state and survive.
        using var database = new TestDatabase();
        var hash = new string('b', 64);
        using var store = SqliteIngestStore.Open(
            database.Path, alertRuleConfigHash: hash);
        await CommitChainAsync(store, count: 2);
        Assert.Equal(2L, Count(database.Path, "alert_queue"));

        IReadOnlyList<PtkSiemReceiver.Configuration.AlertRule> rules =
            [new("completed", "event_match", "tool.completed", null, null)];
        while (await store.EvaluateNextAlertWorkItemAsync(
                   rules, hash, Receipt.ReceivedUtc, CancellationToken.None) is not null)
        {
        }

        Assert.Equal(0L, Count(database.Path, "alert_queue"));
        Assert.Equal(2L, Count(database.Path, "alerts"));

        // Close the first alert; leave the second open.
        Assert.Equal(
            AlertTransitionOutcome.Ok,
            await store.TransitionAlertAsync(1, "acknowledged", Receipt, default));
        Assert.Equal(
            AlertTransitionOutcome.Ok,
            await store.TransitionAlertAsync(1, "closed", Receipt, default));

        _ = await store.EnforceRetentionAsync(
            maximumAgeDays: 30,
            maximumTotalBytes: null,
            utcNow: Receipt.ReceivedUtc.AddYears(1),
            CancellationToken.None);

        Assert.Equal(1L, Count(database.Path, "alerts"));
        Assert.Equal(
            "open",
            Scalar<string>(database.Path, "SELECT state FROM alerts;"));
        Assert.Equal(
            1L,
            Scalar<long>(
                database.Path,
                "SELECT COUNT(*) FROM custody WHERE disposition = 'alert:retention_deleted';"));
    }

    private static string? LastHash { get; set; }

    private static async Task CommitChainAsync(
        SqliteIngestStore store,
        int count,
        IngestReceiptContext? receipt = null,
        int startSequence = 1,
        string? previousHash = null)
    {
        var effectiveReceipt = receipt ?? Receipt;
        for (var index = startSequence; index < startSequence + count; index++)
        {
            var request = OtlpTestRequest.Create(
                eventId: $"018f6a78-4c20-7a11-8a34-1234567890{index:x2}",
                sequence: index,
                previousEventHash: previousHash);
            var record = Validate(request);
            var result = await store.CommitAsync(
                record,
                effectiveReceipt,
                CancellationToken.None);
            Assert.Equal(IngestCommitResultKind.Accepted, result.Kind);
            previousHash = record.EventHash;
        }
        LastHash = previousHash;
    }

    private static ValidatedOtlpRecord Validate(ExportLogsServiceRequest request)
    {
        var validation = OtlpRequestValidator.Validate(request.ToByteArray());
        Assert.Null(validation.FailureCode);
        return Assert.IsType<ValidatedOtlpRecord>(validation.Record);
    }

    private static T Scalar<T>(string path, string sql)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(
            command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static long Count(string path, string table)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _root =
            SiemTestFileSystem.CreateProtectedRoot("ptk-siem-retention");

        internal TestDatabase()
        {
            Path = System.IO.Path.Combine(_root, "siem.db");
        }

        internal string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* Preserve the assertion failure that prevented cleanup. */ }
        }
    }
}
