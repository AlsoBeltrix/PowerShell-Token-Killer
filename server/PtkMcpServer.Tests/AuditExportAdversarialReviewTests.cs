using System.Net;
using System.Text;
using PtkMcpServer.Audit;
using PtkMcpServer.Audit.Export;

namespace PtkMcpServer.Tests;

/// <summary>
/// Independent adversarial review probes (second-opinion pass). Each test
/// encodes a sequence in which records that existed locally are never
/// delivered; the assertion demands SOME loss signal (EXPORT_GAPS or
/// unverified_boot_boundaries). A failing test here is a reproduced silent
/// loss path at head.
/// </summary>
public sealed class AuditExportAdversarialReviewTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* Preserve the assertion failure that prevented cleanup. */ }
        }
    }

    // OPEN, by design: an entire supervisor boot whose records were journaled
    // and destroyed undelivered is structurally invisible to a chain walk,
    // because boot ids are random UUIDv4 with no lineage or counter. Closing
    // it needs a PRODUCER SCHEMA change (boot lineage in the record), which
    // belongs to R3d, not the exporter. Reproduction preserved, not deleted;
    // see .agents/review/findings/cr3-2.md.
    [Fact(Skip = "Open finding: needs producer boot lineage (R3d); reproduction preserved")]
    public async Task Review_a_wholly_vanished_intermediate_boot_leaves_no_signal()
    {
        // Boot A ends cleanly: its lifecycle terminal is delivered, so the
        // durable memory says (A, terminal=true). Boot B then runs briefly,
        // journals records, and crashes; before any drain sees them (the
        // destination was down for those two seconds, or the pump simply had
        // not ticked), retention removes B's only segment. Boot C starts a
        // clean chain at 1.
        //
        // Detection compares C's first record against (A, terminal): C starts
        // at 1 (no gap) and A was terminal (no unverified boundary). Boot B's
        // records existed locally, were never delivered, and no signal of any
        // kind appears — boot ids are random UUIDs with no lineage, so a
        // vanished WHOLE boot is invisible to the chain walk.
        var root = NewRoot("adv-vanished-boot");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var bootA = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var bootB = "5b0e5efc-2f63-46f5-93a3-2f4ea18d6a01";
        var bootC = "2a6465d4-6652-4ff7-8630-2ab0c5f6d04c";
        WriteSegment(options, index: 0, ticksOffset: 0, records:
        [
            ChainRecord(bootA, sequence: 1),
            ChainRecord(bootA, sequence: 2, eventType: "server.stopped"),
        ]);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        Assert.Equal(2, await service.DrainOnceAsync(CancellationToken.None));

        // Boot B's entire chain is journaled and then removed before any
        // drain.
        var bootBSegment = WriteSegment(options, index: 0, ticksOffset: 1, records:
        [
            ChainRecord(bootB, sequence: 1),
            ChainRecord(bootB, sequence: 2),
        ], bootId: bootB);
        File.Delete(bootBSegment);

        WriteSegment(options, index: 0, ticksOffset: 2, records:
        [
            ChainRecord(bootC, sequence: 1),
        ], bootId: bootC);
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));

        var line = health.Snapshot().StatusLine();
        Assert.True(
            line.Contains("EXPORT_GAPS", StringComparison.Ordinal) ||
            line.Contains("unverified_boot_boundaries", StringComparison.Ordinal),
            $"boot B's records were journaled and destroyed undelivered, yet: {line}");
    }

    [Fact]
    public async Task Review_a_corrupt_ledger_behind_a_healthy_cursor_erases_proved_gaps()
    {
        // A gap is proved and durably recorded (EXPORT_GAPS=1 on the ledger).
        // The ledger file is then corrupted while the CURSOR stays healthy.
        // ReadOrQuarantine only runs from EffectivePriorPosition, and only
        // when the cursor lacks a chain position — with a healthy cursor the
        // corrupt ledger is read through the lenient Read() path, which
        // swallows the corruption and reports Empty. The restarted process
        // shows no EXPORT_GAPS, no boundary, and no quarantine; the next
        // RecordChainPosition write then replaces the corrupt file, so the
        // evidence is gone durably too.
        var root = NewRoot("adv-corrupt-ledger-healthy-cursor");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var boot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var first = WriteSegment(options, index: 0, ticksOffset: 0, records:
        [
            ChainRecord(boot, sequence: 1),
        ]);

        using var receiver = new FakeHttpDestination();
        var cursorStore = new AuditExportCursorStore(root);
        var health = new AuditExportHealth();
        await using (var service = NewService(options, receiver, cursorStore, health))
        {
            Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
            // Sequences 2-3 are removed before delivery: a proved gap.
            File.Delete(first);
            WriteSegment(options, index: 1, ticksOffset: 1, records:
            [
                ChainRecord(boot, sequence: 4),
            ]);
            Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
            Assert.Contains(
                "EXPORT_GAPS=1",
                health.Snapshot().StatusLine(),
                StringComparison.Ordinal);
        }

        // The ledger is corrupted; the cursor is untouched and healthy.
        var ledgerPath = Path.Combine(root, AuditExportGapStore.FileName);
        Assert.True(File.Exists(ledgerPath), "the durable ledger was never written");
        File.WriteAllText(ledgerPath, "{ this is not valid json");

        var restartedHealth = new AuditExportHealth();
        await using var restarted = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            restartedHealth);
        Assert.Equal(0, await restarted.DrainOnceAsync(CancellationToken.None));

        var line = restartedHealth.Snapshot().StatusLine();
        Assert.True(
            line.Contains("EXPORT_GAPS", StringComparison.Ordinal) ||
            line.Contains("unverified_boot_boundaries", StringComparison.Ordinal),
            $"a proved, durably recorded gap disappeared without a trace: {line}");
    }

    [Fact]
    public async Task Review_both_stores_unwritable_loses_the_gap_across_restart_while_execution_continues()
    {
        // The round-9 residual argument was: "if BOTH cursor and ledger are
        // unwritable, the audit root is failing and local journaling is
        // fail-closed, so execution stops first." But the journal writes into
        // spool/, a subdirectory — the two stores write into the ROOT. A root
        // that refuses those two paths (here: directories squatting on both
        // file names, exactly the round-9 technique) leaves journaling and
        // delivery running. A gap proved in that state lives only in process
        // memory; once retention removes the jump evidence, a restart reports
        // fully healthy.
        var root = NewRoot("adv-both-stores-unwritable");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        Directory.CreateDirectory(Path.Combine(root, AuditExportGapStore.FileName));
        Directory.CreateDirectory(Path.Combine(root, AuditExportCursorStore.FileName));
        var boot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var newBoot = "2a6465d4-6652-4ff7-8630-2ab0c5f6d04c";
        var segment = WriteSegment(options, index: 0, ticksOffset: 0, records:
        [
            ChainRecord(boot, sequence: 1),
        ]);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using (var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health))
        {
            // CONTRACT CHANGED by the fix for this finding: with no writable
            // export metadata, export PAUSES before delivering rather than
            // delivering with nothing durable behind it. Journaling is
            // untouched, so execution still never stops.
            Assert.Equal(0, await service.DrainOnceAsync(CancellationToken.None));
            AppendRecords(segment, [ChainRecord(boot, sequence: 3)]);
            Assert.Equal(0, await service.DrainOnceAsync(CancellationToken.None));
            Assert.Equal(
                "export.metadata_unwritable",
                health.Snapshot().LastFailureDetail);
        }

        // Crash. Retention removes the old boot's segment; the next boot
        // starts a clean chain.
        File.Delete(segment);
        WriteSegment(options, index: 0, ticksOffset: 1, records:
        [
            ChainRecord(newBoot, sequence: 1),
        ], bootId: newBoot);

        var restartedHealth = new AuditExportHealth();
        await using var restarted = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            restartedHealth);
        // Nothing was delivered while metadata was unwritable, so nothing was
        // lost to the restart: the records stay spooled and are delivered (or
        // their loss proved) once metadata works again.
        _ = await restarted.DrainOnceAsync(CancellationToken.None);
        var line = restartedHealth.Snapshot().StatusLine();
        Assert.True(
            line.Contains("EXPORT_GAPS", StringComparison.Ordinal) ||
            line.Contains("unverified_boot_boundaries", StringComparison.Ordinal) ||
            line.Contains("retrying", StringComparison.Ordinal),
            $"neither loss nor the blocked-metadata condition was reported: {line}");
    }

    [Fact]
    public async Task Review_a_refused_record_leaves_no_signal_after_the_next_success()
    {
        // cr3-5 isolates a permanently refused record and steps over it:
        // "reported and stepped over". The report is RecordFailure, which the
        // very next successful delivery resets (ConsecutiveFailures = 0,
        // LastFailureDetail = null when ExportGaps == 0). The refused record
        // existed locally, is never delivered, the cursor's chain position
        // absorbs it as if delivered, and within seconds the exporter reports
        // fully healthy with no EXPORT_GAPS and no boundary — in-process, and
        // a fortiori after restart.
        var root = NewRoot("adv-refused-record");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var boot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var segment = WriteSegment(options, index: 0, ticksOffset: 0, records:
        [
            ChainRecord(boot, sequence: 1),
            ChainRecord(boot, sequence: 2, eventId: "poison"),
            ChainRecord(boot, sequence: 3),
        ]);

        using var receiver = new FakeHttpDestination
        {
            RefusePredicate = body => body.Contains("poison", StringComparison.Ordinal),
            RefusalStatus = HttpStatusCode.BadRequest,
        };
        var health = new AuditExportHealth();
        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        Assert.Equal(2, await service.DrainOnceAsync(CancellationToken.None));

        // The next ordinary delivery succeeds.
        AppendRecords(segment, [ChainRecord(boot, sequence: 4)]);
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));

        var snapshot = health.Snapshot();
        var line = snapshot.StatusLine();
        Assert.True(
            snapshot.ConsecutiveFailures > 0 ||
            snapshot.LastFailureDetail is not null ||
            line.Contains("EXPORT_GAPS", StringComparison.Ordinal) ||
            line.Contains("unverified_boot_boundaries", StringComparison.Ordinal),
            $"a record was permanently refused (never delivered) yet: {line}");
    }

    private static AuditExportService NewService(
        AuditOptions options,
        FakeHttpDestination receiver,
        AuditExportCursorStore cursorStore,
        AuditExportHealth health)
    {
        var settings = new AuditExportSettings(
            AuditDestinationKind.OtlpHttp,
            receiver.BaseUri,
            null);
        return new AuditExportService(
            options,
            new HttpAuditDestination(settings),
            cursorStore,
            health);
    }

    private static string ChainRecord(
        string supervisorBootId,
        long sequence,
        string eventType = "call.completed",
        string? eventId = null) =>
        "{\"schema_version\":\"ptk.audit/2\",\"event_id\":\"" +
        (eventId ?? ("019f5ee1-2384-7eac-8f88-2eb4e7ec5e" +
            sequence.ToString("D2", System.Globalization.CultureInfo.InvariantCulture))) +
        "\",\"event_type\":\"" + eventType + "\",\"sequence\":" +
        sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"producer\":{\"host_id\":\"92874c03-05a7-4aa6-8094-b2e87cad5696\"," +
        "\"supervisor_boot_id\":\"" + supervisorBootId + "\",\"worker_boot_id\":null," +
        "\"pid\":32890,\"version\":\"1.0.0.0\",\"binary_digest\":null}}";

    private static string WriteSegment(
        AuditOptions options,
        int index,
        int ticksOffset,
        string[] records,
        string? bootId = null)
    {
        var identity = AuditSpoolSegmentIdentity.Create(
            bootId is null ? Guid.NewGuid() : Guid.Parse(bootId),
            index);
        var path = Path.Combine(options.SpoolDirectory, identity.FileName);
        File.WriteAllText(path, string.Concat(records.Select(record => record + "\n")));
        // Deterministic creation-time ordering: the exporter enumerates by
        // CreationTimeUtc, and tests must not depend on filesystem timestamp
        // granularity.
        File.SetCreationTimeUtc(
            path,
            new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc).AddSeconds(ticksOffset));
        return path;
    }

    private static void AppendRecords(string segmentPath, string[] records) =>
        File.AppendAllText(
            segmentPath,
            string.Concat(records.Select(record => record + "\n")));

    private string NewRoot(string label)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            $"test-{label}-{Guid.NewGuid():N}");
        _roots.Add(root);
        return SecureAuditStorage.PrepareRoot(root);
    }

    private sealed class FakeHttpDestination : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _loop;

        internal FakeHttpDestination()
        {
            var port = FreePort();
            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(BaseUri.ToString());
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        internal Uri BaseUri { get; }

        internal HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;

        internal Func<string, bool>? RefusePredicate { get; set; }

        internal HttpStatusCode RefusalStatus { get; set; } = HttpStatusCode.BadRequest;

        internal List<string> Bodies { get; } = [];

        private async Task AcceptAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                using var reader = new StreamReader(
                    context.Request.InputStream,
                    Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                lock (Bodies) Bodies.Add(body);
                context.Response.StatusCode = RefusePredicate?.Invoke(body) == true
                    ? (int)RefusalStatus
                    : (int)ResponseStatus;
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _stopping.Cancel();
            try { _listener.Stop(); } catch { /* already stopped */ }
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { /* shutting down */ }
            _listener.Close();
            _stopping.Dispose();
        }
    }
}
