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

    private static async Task CommitChainAsync(SqliteIngestStore store, int count)
    {
        string? previousHash = null;
        for (var index = 1; index <= count; index++)
        {
            var request = OtlpTestRequest.Create(
                eventId: $"018f6a78-4c20-7a11-8a34-1234567890{index:x2}",
                sequence: index,
                previousEventHash: previousHash);
            var record = Validate(request);
            var result = await store.CommitAsync(record, Receipt, CancellationToken.None);
            Assert.Equal(IngestCommitResultKind.Accepted, result.Kind);
            previousHash = record.EventHash;
        }
    }

    private static ValidatedOtlpRecord Validate(ExportLogsServiceRequest request)
    {
        var validation = OtlpRequestValidator.Validate(request.ToByteArray());
        Assert.Null(validation.FailureCode);
        return Assert.IsType<ValidatedOtlpRecord>(validation.Record);
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
