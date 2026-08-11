using System.Net;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Audit;
using PtkMcpServer.Audit.Export;

namespace PtkMcpServer.Tests;

public sealed class AuditExportTests : IDisposable
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

    [Fact]
    public void Settings_require_tls_for_every_nonloopback_destination()
    {
        var root = NewRoot("export-settings");
        // Plaintext to a remote SIEM is refused outright: a credential must
        // never cross the network in the clear.
        WriteSettings(root, "otlp_http", "http://siem.example.com/v1/logs", "token");
        var remote = AuditExportSettings.Load(root, out var remoteFailure);
        Assert.False(remote.IsConfigured);
        Assert.Equal("export.endpoint_invalid", remoteFailure);

        // Loopback plaintext IS allowed: that is the zero-config local
        // fallback receiver, never a network hop.
        WriteSettings(root, "otlp_http", "http://127.0.0.1:4318/v1/logs", "token");
        var loopback = AuditExportSettings.Load(root, out var loopbackFailure);
        Assert.True(loopback.IsConfigured);
        Assert.Null(loopbackFailure);

        WriteSettings(root, "splunk_hec", "https://splunk.example.com:8088", "hec-token");
        var splunk = AuditExportSettings.Load(root, out _);
        Assert.Equal(AuditDestinationKind.SplunkHec, splunk.Kind);
        Assert.True(splunk.IsConfigured);
    }

    [Fact]
    public void Settings_never_describe_a_destination_with_its_credential()
    {
        var root = NewRoot("export-describe");
        WriteSettings(root, "splunk_hec", "https://splunk.example.com:8088", "SUPER-SECRET-TOKEN");
        var settings = AuditExportSettings.Load(root, out _);
        var description = settings.Describe();
        Assert.DoesNotContain("SUPER-SECRET-TOKEN", description, StringComparison.Ordinal);
        Assert.Contains("splunk_hec", description, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_export_configuration_disables_delivery_without_throwing()
    {
        var root = NewRoot("export-broken-config");
        File.WriteAllText(Path.Combine(root, AuditExportSettings.FileName), "{ not json");
        var settings = AuditExportSettings.Load(root, out var failure);
        Assert.False(settings.IsConfigured);
        Assert.Equal("export.configuration_unreadable", failure);
    }

    [Fact]
    public async Task Records_reach_a_splunk_hec_destination_with_its_token()
    {
        using var receiver = new FakeHttpDestination();
        var settings = new AuditExportSettings(
            AuditDestinationKind.SplunkHec,
            receiver.BaseUri,
            "hec-token");
        using var destination = new HttpAuditDestination(settings);

        var result = await destination.DeliverAsync(
            ["""{"event_type":"call.completed","event_id":"e1"}"""],
            CancellationToken.None);

        Assert.Equal(AuditDeliveryDisposition.Delivered, result.Disposition);
        var request = Assert.Single(receiver.Requests);
        Assert.Equal("/services/collector/event", request.Path);
        Assert.Equal("Splunk hec-token", request.Authorization);
        Assert.Contains("\"sourcetype\":\"ptk:audit\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("call.completed", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Records_reach_an_otlp_destination_as_valid_otlp_json_logs()
    {
        using var receiver = new FakeHttpDestination();
        var settings = new AuditExportSettings(
            AuditDestinationKind.OtlpHttp,
            receiver.BaseUri,
            "bearer-token");
        using var destination = new HttpAuditDestination(settings);

        var result = await destination.DeliverAsync(
            [
                """{"event_type":"call.completed","event_id":"e1","observed_utc":"2026-08-10T12:00:00.0000000Z"}""",
                """{"event_type":"server.started","event_id":"e2"}""",
            ],
            CancellationToken.None);

        Assert.Equal(AuditDeliveryDisposition.Delivered, result.Disposition);
        var request = Assert.Single(receiver.Requests);
        Assert.Equal("/v1/logs", request.Path);
        Assert.Equal("Bearer bearer-token", request.Authorization);

        // The payload must be well-formed OTLP/HTTP JSON logs, not a
        // hand-rolled shape a collector would reject.
        using var document = JsonDocument.Parse(request.Body);
        var logRecords = document.RootElement
            .GetProperty("resourceLogs")[0]
            .GetProperty("scopeLogs")[0]
            .GetProperty("logRecords");
        Assert.Equal(2, logRecords.GetArrayLength());
        var first = logRecords[0];
        Assert.Contains(
            "call.completed",
            first.GetProperty("body").GetProperty("stringValue").GetString(),
            StringComparison.Ordinal);
        // 2026-08-10T12:00:00Z in nanoseconds: the record's own observed_utc
        // carries into OTLP, not the delivery time.
        Assert.Equal(
            "1786363200000000000",
            first.GetProperty("timeUnixNano").GetString());
        Assert.Contains(
            first.GetProperty("attributes").EnumerateArray(),
            attribute => attribute.GetProperty("key").GetString() == "ptk.event_type");
    }

    [Fact]
    public async Task A_malformed_record_is_still_delivered_rather_than_dropped()
    {
        using var receiver = new FakeHttpDestination();
        var settings = new AuditExportSettings(
            AuditDestinationKind.OtlpHttp,
            receiver.BaseUri,
            null);
        using var destination = new HttpAuditDestination(settings);

        var result = await destination.DeliverAsync(
            ["this is not json at all"],
            CancellationToken.None);

        Assert.Equal(AuditDeliveryDisposition.Delivered, result.Disposition);
        var request = Assert.Single(receiver.Requests);
        Assert.Contains("this is not json at all", request.Body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(request.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "retryable")]
    [InlineData(HttpStatusCode.TooManyRequests, "retryable")]
    [InlineData(HttpStatusCode.Unauthorized, "retryable")]
    [InlineData(HttpStatusCode.BadRequest, "permanent")]
    public async Task Destination_responses_map_to_retry_or_permanent_dispositions(
        HttpStatusCode status,
        string expectedDisposition)
    {
        var expected = expectedDisposition == "retryable"
            ? AuditDeliveryDisposition.Retryable
            : AuditDeliveryDisposition.Permanent;
        using var receiver = new FakeHttpDestination { ResponseStatus = status };
        var settings = new AuditExportSettings(
            AuditDestinationKind.OtlpHttp,
            receiver.BaseUri,
            null);
        using var destination = new HttpAuditDestination(settings);

        var result = await destination.DeliverAsync(
            ["""{"event_type":"call.completed"}"""],
            CancellationToken.None);

        Assert.Equal(expected, result.Disposition);
    }

    [Fact]
    public async Task The_export_service_drains_the_spool_and_resumes_from_its_cursor()
    {
        var root = NewRoot("export-drain");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var segment = WriteSegment(options, index: 0, records:
        [
            """{"event_type":"server.started","event_id":"e1"}""",
            """{"event_type":"call.completed","event_id":"e2"}""",
        ]);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        var cursorStore = new AuditExportCursorStore(root);
        await using (var service = NewService(options, receiver, cursorStore, health))
        {
            Assert.Equal(2, await service.DrainOnceAsync(CancellationToken.None));
            // A second pass with nothing new delivers nothing: the cursor is
            // durable, so records are not re-sent on every tick.
            Assert.Equal(0, await service.DrainOnceAsync(CancellationToken.None));
        }

        var position = PositionFor(cursorStore.Read(), segment);
        Assert.Equal(Path.GetFileName(segment), position.SegmentFileName);
        Assert.Equal(new FileInfo(segment).Length, position.ByteOffset);

        // A fresh service (restart) resumes after the cursor, then picks up
        // records appended since.
        AppendRecords(segment, ["""{"event_type":"call.completed","event_id":"e3"}"""]);
        var health2 = new AuditExportHealth();
        await using var resumed = NewService(options, receiver, new AuditExportCursorStore(root), health2);
        Assert.Equal(1, await resumed.DrainOnceAsync(CancellationToken.None));

        var delivered = receiver.Requests
            .SelectMany(request => JsonDocument.Parse(request.Body).RootElement
                .GetProperty("resourceLogs")[0]
                .GetProperty("scopeLogs")[0]
                .GetProperty("logRecords")
                .EnumerateArray()
                .Select(record => record.GetProperty("body").GetProperty("stringValue").GetString()!))
            .ToArray();
        Assert.Equal(3, delivered.Length);
        Assert.Contains(delivered, text => text.Contains("e3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_torn_trailing_record_is_left_for_the_next_pass()
    {
        var root = NewRoot("export-torn");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var segment = WriteSegment(options, index: 0, records:
        [
            """{"event_type":"server.started","event_id":"e1"}""",
        ]);
        // A record the writer has not finished flushing: no trailing newline.
        File.AppendAllText(segment, """{"event_type":"call.completed","event_id":"tor""");

        using var receiver = new FakeHttpDestination();
        var cursorStore = new AuditExportCursorStore(root);
        await using var service = NewService(options, receiver, cursorStore, new AuditExportHealth());
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));

        var body = Assert.Single(receiver.Requests).Body;
        Assert.DoesNotContain("\"tor", body, StringComparison.Ordinal);

        // Once the writer completes the record, it is delivered whole.
        File.AppendAllText(segment, "ndelivered\"}\n");
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
        Assert.Contains(
            "torndelivered",
            receiver.Requests[^1].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_destination_holds_the_cursor_and_reports_health()
    {
        var root = NewRoot("export-failing");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        WriteSegment(options, index: 0, records:
        [
            """{"event_type":"server.started","event_id":"e1"}""",
        ]);

        using var receiver = new FakeHttpDestination
        {
            ResponseStatus = HttpStatusCode.ServiceUnavailable,
        };
        var health = new AuditExportHealth();
        var cursorStore = new AuditExportCursorStore(root);
        await using var service = NewService(options, receiver, cursorStore, health);

        Assert.Equal(0, await service.DrainOnceAsync(CancellationToken.None));
        // Nothing acknowledged means nothing skipped: the cursor stays put so
        // the outage costs lag, never lost custody.
        Assert.Empty(cursorStore.Read().Boots);

        var snapshot = health.Snapshot();
        Assert.True(snapshot.Configured);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
        Assert.Equal("export.http_503", snapshot.LastFailureDetail);
        Assert.True(snapshot.PendingBytes > 0);
        Assert.Contains("retrying", snapshot.StatusLine(), StringComparison.Ordinal);

        // When the destination recovers, the same records are delivered.
        receiver.ResponseStatus = HttpStatusCode.OK;
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
        Assert.Equal(0, health.Snapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task A_permanently_refused_batch_does_not_wedge_later_records()
    {
        var root = NewRoot("export-refused");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var segment = WriteSegment(options, index: 0, records:
        [
            """{"event_type":"server.started","event_id":"e1"}""",
        ]);

        using var receiver = new FakeHttpDestination
        {
            ResponseStatus = HttpStatusCode.BadRequest,
        };
        var health = new AuditExportHealth();
        var cursorStore = new AuditExportCursorStore(root);
        await using var service = NewService(options, receiver, cursorStore, health);

        await service.DrainOnceAsync(CancellationToken.None);
        Assert.Equal("export.http_400", health.Snapshot().LastFailureDetail);
        // Stepped over, and reported — a poison batch cannot block custody of
        // everything recorded after it.
        Assert.Equal(
            Path.GetFileName(segment),
            PositionFor(cursorStore.Read(), segment).SegmentFileName);

        receiver.ResponseStatus = HttpStatusCode.OK;
        AppendRecords(segment, ["""{"event_type":"call.completed","event_id":"e2"}"""]);
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unreadable_segment_is_reported_rather_than_read_as_empty()
    {
        // cr3-1: every read failure became an empty batch, so an exporter
        // that could deliver nothing still reported "healthy". The live
        // journal segment is genuinely unreadable — the writer holds it
        // FileShare.None and that exclusivity is load-bearing for its own
        // live-vs-closed classification — so this condition is real, and it
        // must be visible.
        var root = NewRoot("export-unreadable");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var segment = WriteSegment(options, index: 0, records:
        [
            """{"event_type":"server.started","event_id":"e1"}""",
        ]);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);

        // Hold the segment exactly as the journal writer holds its live one.
        using (new FileStream(
            segment,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            Assert.Equal(0, await service.DrainOnceAsync(CancellationToken.None));
        }

        var snapshot = health.Snapshot();
        Assert.Equal("export.segment_unreadable", snapshot.LastFailureDetail);
        Assert.True(snapshot.ConsecutiveFailures > 0);
        Assert.DoesNotContain("healthy", snapshot.StatusLine(), StringComparison.Ordinal);

        // Once the writer releases it, the records deliver normally.
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
        Assert.Equal(0, health.Snapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task The_live_tail_exports_through_the_writers_handle_before_rotation()
    {
        // cr3-1's completion (R3d): the journal writer holds the live segment
        // FileShare.None — load-bearing for its live-vs-closed classification
        // — so the exporter reads the durably committed tail through the
        // writer's OWN handle. Records journaled seconds ago reach the SIEM
        // before any rotation, and a drained live tail is quiet, not a
        // permanently reported failure.
        var root = NewRoot("export-live-tail");
        var options = AuditOptions.Create(root);
        var health = new AuditExportHealth();
        using var receiver = new FakeHttpDestination();
        using var journal = AuditJournalFactory.Open(
            options,
            new AuditHealth(options),
            "test-version");
        AppendJournalEvents(journal, 3);

        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health,
            liveJournalSource: () => journal);

        Assert.Equal(3, await service.DrainOnceAsync(CancellationToken.None));
        var snapshot = health.Snapshot();
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Null(snapshot.LastFailureDetail);

        // Fully delivered: the next pass is quiet — no delivery, no failure.
        Assert.Equal(0, await service.DrainOnceAsync(CancellationToken.None));
        Assert.Equal(0, health.Snapshot().ConsecutiveFailures);
        Assert.Null(health.Snapshot().LastFailureDetail);

        // New records become deliverable on the very next drain, still live.
        AppendJournalEvents(journal, 2);
        Assert.Equal(2, await service.DrainOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Live_then_rotated_delivery_is_continuous_with_neither_gaps_nor_duplicates()
    {
        // The live read hands over to the ordinary closed-file read at
        // rotation through one shared currency: file byte offsets on the
        // cursor. Every journaled record must be delivered exactly once
        // across that boundary, with no chain gap manufactured.
        var root = NewRoot("export-live-rotation");
        var options = AuditOptions.Create(
            root,
            AuditProtectionMode.LocalOnly,
            exportConfigurationIdentity: null,
            maxRecordBytes: 4096,
            segmentBytes: 16_384,
            aggregateBytes: 1024 * 1024,
            emergencyReserveBytes: 8192,
            retentionAge: TimeSpan.FromDays(1),
            maxEvidenceBytes: 4096,
            evidenceAggregateBytes: 4096,
            evidenceRetentionAge: TimeSpan.FromDays(1));
        var health = new AuditExportHealth();
        using var receiver = new FakeHttpDestination();
        using var journal = AuditJournalFactory.Open(
            options,
            new AuditHealth(options),
            "test-version");
        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health,
            liveJournalSource: () => journal);

        // Some records are exported straight off the live tail...
        var appended = AppendJournalEvents(journal, 3);
        var delivered = await service.DrainOnceAsync(CancellationToken.None);

        // ...then the segment rotates with records the live pass never saw.
        var bootId = journal.SupervisorBootId;
        while (!File.Exists(Path.Combine(
                   options.SpoolDirectory,
                   AuditSpoolSegmentIdentity.Create(bootId, 1).FileName)))
        {
            appended += AppendJournalEvents(journal, 1);
        }
        appended += AppendJournalEvents(journal, 2);

        delivered += await service.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(appended, delivered);
        var line = health.Snapshot().StatusLine();
        Assert.DoesNotContain("EXPORT_GAPS", line, StringComparison.Ordinal);
        Assert.DoesNotContain("unverified_boot_boundaries", line, StringComparison.Ordinal);
        Assert.Equal(0, health.Snapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task A_foreign_live_segment_halts_its_boot_and_no_position_passes_it()
    {
        // cr4-4 (per-boot positions): another supervisor's live segment is
        // unreadable here (FileShare.None) and not servable by this process's
        // live source. Its BOOT halts — no position for it may advance, so
        // retention keeps protecting it — while every other boot's delivery
        // proceeds independently on its own position.
        var root = NewRoot("export-foreign-live");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var bootA = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var bootB = "5b0e5efc-2f63-46f5-93a3-2f4ea18d6a01";
        var baseTime = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
        var foreignLive = WriteSegment(
            options, index: 0, [ChainRecord(bootA, 1)], bootA, baseTime);
        WriteSegment(
            options, index: 0, [ChainRecord(bootB, 1)], bootB, baseTime.AddSeconds(1));

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        var cursorStore = new AuditExportCursorStore(root);
        await using var service = NewService(options, receiver, cursorStore, health);

        using (new FileStream(
            foreignLive,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            // The unaffected boot delivers; the locked boot is reported and
            // records NO position, so nothing can ever have "passed" it.
            Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
            Assert.Equal("export.segment_unreadable", health.Snapshot().LastFailureDetail);
            Assert.Null(cursorStore.Read().For(Guid.Parse(bootA)));
            Assert.NotNull(cursorStore.Read().For(Guid.Parse(bootB)));
        }

        // Once the foreign writer releases (rotation/exit), its records
        // deliver with no gap manufactured.
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
        Assert.DoesNotContain(
            "EXPORT_GAPS",
            health.Snapshot().StatusLine(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_delivered_boots_segments_never_hides_an_undelivered_boot()
    {
        // The cr4-4 REOPEN scenario: with the old single cursor over a
        // mutable file ordering, deleting delivered segments re-sorted an
        // undelivered boot to "before the cursor", which both skipped it and
        // let retention classify it delivered. Per-boot positions make the
        // sequence deliver correctly by construction.
        var root = NewRoot("export-reopen-scenario");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var bootA = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var bootB = "5b0e5efc-2f63-46f5-93a3-2f4ea18d6a01";
        var baseTime = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
        var segmentA0 = WriteSegment(
            options, index: 0, [ChainRecord(bootA, 1)], bootA, baseTime);
        WriteSegment(
            options, index: 1, [ChainRecord(bootA, 2)], bootA, baseTime.AddSeconds(2));
        var lockedB = WriteSegment(
            options, index: 0, [ChainRecord(bootB, 1)], bootB, baseTime.AddSeconds(1));

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        var cursorStore = new AuditExportCursorStore(root);
        await using var service = NewService(options, receiver, cursorStore, health);

        // Boot A delivers fully while boot B is another supervisor's live
        // segment.
        using (new FileStream(
            lockedB,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            Assert.Equal(2, await service.DrainOnceAsync(CancellationToken.None));
        }

        // Retention removes A's delivered first segment; under the old
        // mutable ordering this re-sorted B before the cursor.
        File.Delete(segmentA0);

        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
        Assert.DoesNotContain(
            "EXPORT_GAPS",
            health.Snapshot().StatusLine(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interleaved_boot_segments_produce_no_false_gaps()
    {
        // cr4-4: two concurrent boots interleave segment creation
        // (A0, B0, A1). Creation-time traversal crossed back into boot A at
        // sequence 3 and recorded a FALSE proved gap A:1-2 for records that
        // were delivered moments earlier. Traversal is grouped per boot.
        var root = NewRoot("export-interleaved");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var bootA = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var bootB = "5b0e5efc-2f63-46f5-93a3-2f4ea18d6a01";
        var baseTime = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
        WriteSegment(options, index: 0,
            [ChainRecord(bootA, 1), ChainRecord(bootA, 2)], bootA, baseTime);
        WriteSegment(options, index: 0,
            [ChainRecord(bootB, 1)], bootB, baseTime.AddSeconds(1));
        WriteSegment(options, index: 1,
            [ChainRecord(bootA, 3)], bootA, baseTime.AddSeconds(2));

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using var service = NewService(
            options, receiver, new AuditExportCursorStore(root), health);

        Assert.Equal(4, await service.DrainOnceAsync(CancellationToken.None));
        Assert.DoesNotContain(
            "EXPORT_GAPS",
            health.Snapshot().StatusLine(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pending_attestation_still_raises_when_the_predecessors_tail_later_vanishes()
    {
        // cr4-4, second frontier round: lineage claims judged only in the
        // drain that read them went silent if the predecessor was merely
        // blocked at that moment and its undelivered tail vanished later.
        // The attestation lives on the successor's durable cursor position
        // and is re-judged every drain — surviving restarts too.
        var root = NewRoot("export-pending-attestation");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var bootP = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var bootB = "5b0e5efc-2f63-46f5-93a3-2f4ea18d6a01";
        var baseTime = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
        WriteSegment(options, index: 0, [ChainRecord(bootP, 1)], bootP, baseTime);
        var lockedTail = WriteSegment(
            options, index: 1, [ChainRecord(bootP, 2)], bootP, baseTime.AddSeconds(1));
        WriteSegment(
            options, index: 0,
            [ChainRecord(bootB, 1, previousBootId: bootP)],
            bootB, baseTime.AddSeconds(2));

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using (var service = NewService(
            options, receiver, new AuditExportCursorStore(root), health))
        {
            using (new FileStream(
                lockedTail,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                // P is merely blocked: correctly no signal yet.
                Assert.Equal(2, await service.DrainOnceAsync(CancellationToken.None));
                Assert.DoesNotContain(
                    "unverified_boot_boundaries",
                    health.Snapshot().StatusLine(),
                    StringComparison.Ordinal);
            }

            // P's undelivered tail vanishes wholly; its writer is gone.
            File.Delete(lockedTail);
            Assert.Equal(0, await service.DrainOnceAsync(CancellationToken.None));
            Assert.Contains(
                "unverified_boot_boundaries=1",
                health.Snapshot().StatusLine(),
                StringComparison.Ordinal);
        }

        // The attestation is durable: a restarted exporter still raises it.
        var restartedHealth = new AuditExportHealth();
        await using var restarted = NewService(
            options, receiver, new AuditExportCursorStore(root), restartedHealth);
        Assert.Equal(0, await restarted.DrainOnceAsync(CancellationToken.None));
        Assert.Contains(
            "unverified_boot_boundaries=1",
            restartedHealth.Snapshot().StatusLine(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_one_exporter_drains_a_shared_root_at_a_time()
    {
        // cr4-4: every supervisor runs an export service against the same
        // durable cursor and ledger; without a lease their tmp+rename writes
        // race last-writer-wins. Exactly one drains; the rest stand by.
        var root = NewRoot("export-lease");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var boot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        WriteSegment(options, index: 0, [ChainRecord(boot, 1)], boot);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using var service = NewService(
            options, receiver, new AuditExportCursorStore(root), health);

        using (new FileStream(
            Path.Combine(root, AuditExportLease.FileName),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            Assert.Equal(0, await service.DrainOnceAsync(CancellationToken.None));
            Assert.Contains(
                "standby",
                health.Snapshot().StatusLine(),
                StringComparison.Ordinal);
        }

        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
        Assert.DoesNotContain(
            "standby",
            health.Snapshot().StatusLine(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Records_removed_before_delivery_are_reported_as_a_durable_gap()
    {
        // cr3-2 (round 2): loss is proved by the chain, not by which files
        // survive. Whatever retention and rotation did, a jump in the
        // per-boot sequence means records were removed before delivery.
        var root = NewRoot("export-gap");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var boot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var first = WriteSegment(options, index: 0, records:
        [
            ChainRecord(boot, sequence: 1),
        ]);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        var cursorStore = new AuditExportCursorStore(root);
        await using var service = NewService(options, receiver, cursorStore, health);
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));

        // Sequences 2 and 3 are written, then removed before any drain sees
        // them — the exact race that defeated file bookkeeping: appended,
        // rotated, deleted between drains.
        File.Delete(first);
        WriteSegment(options, index: 1, records:
        [
            ChainRecord(boot, sequence: 4),
        ]);

        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
        var snapshot = health.Snapshot();
        // Asserted through the stable status-line surface so this guard
        // compiles — and therefore bites — against every prior revision.
        Assert.Equal(1, snapshot.ExportGaps);
        Assert.Contains("EXPORT_GAPS=1", snapshot.StatusLine(), StringComparison.Ordinal);
        Assert.Contains("missing_records=2", snapshot.StatusLine(), StringComparison.Ordinal);

        // The gap is evidence and survives the process.
        var restartedHealth = new AuditExportHealth();
        await using var restarted = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            restartedHealth);
        Assert.Equal(1, restartedHealth.Snapshot().ExportGaps);
        Assert.Contains(
            "missing_records=2",
            restartedHealth.Snapshot().StatusLine(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotation_and_retention_of_delivered_records_raise_no_gap()
    {
        // The false-positive half: deleting records that WERE delivered is
        // retention working. A contiguous chain proves nothing was lost, so
        // no alarm — including across a segment rotation.
        var root = NewRoot("export-no-false-gap");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var boot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var first = WriteSegment(options, index: 0, records:
        [
            ChainRecord(boot, sequence: 1),
            ChainRecord(boot, sequence: 2),
        ]);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        Assert.Equal(2, await service.DrainOnceAsync(CancellationToken.None));

        // Rotate, then retention removes the fully delivered segment.
        WriteSegment(options, index: 1, records:
        [
            ChainRecord(boot, sequence: 3),
        ]);
        File.Delete(first);
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));

        Assert.Equal(0, health.Snapshot().ExportGaps);
        Assert.DoesNotContain(
            "EXPORT_GAPS",
            health.Snapshot().StatusLine(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_first_run_starting_above_sequence_one_reports_the_lost_prefix()
    {
        // cr3-2 round 4: with no cursor yet -- a first run, or a cursor lost
        // or corrupted -- the first record was never inspected, so an outage
        // plus retention could leave the exporter starting mid-chain with no
        // signal. Every chain starts at 1.
        var root = NewRoot("export-first-run-gap");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var boot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        // Sequences 1-4 were deleted before this exporter ever ran.
        WriteSegment(options, index: 0, records:
        [
            ChainRecord(boot, sequence: 5),
        ]);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));

        var line = health.Snapshot().StatusLine();
        Assert.Contains("EXPORT_GAPS=1", line, StringComparison.Ordinal);
        Assert.Contains("missing_records=4", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_erased_boot_survives_cursor_loss_as_an_unverified_boundary()
    {
        // cr3-2 round 5: with the cursor lost, retention erasing an old boot
        // (delivered sequence 1, undelivered 2-3) and a restart whose new
        // boot begins cleanly at 1 reported healthy — the memory boundary
        // detection depends on lived only on the cursor. The durable ledger
        // now keeps it.
        var root = NewRoot("export-erased-boot");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var oldBoot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var first = WriteSegment(options, index: 0, records:
        [
            ChainRecord(oldBoot, sequence: 1),
        ]);

        using var receiver = new FakeHttpDestination();
        var cursorStore = new AuditExportCursorStore(root);
        await using (var service = NewService(
            options,
            receiver,
            cursorStore,
            new AuditExportHealth()))
        {
            Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
        }

        // The cursor is lost, the old boot's segments (including its
        // undelivered sequences 2-3) are erased, and a new boot starts clean.
        File.Delete(cursorStore.CursorPath);
        File.Delete(first);
        WriteSegment(options, index: 1, records:
        [
            ChainRecord(
                "2a6465d4-6652-4ff7-8630-2ab0c5f6d04c",
                sequence: 1,
                previousBootId: oldBoot),
        ]);

        var health = new AuditExportHealth();
        await using var restarted = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        Assert.Equal(1, await restarted.DrainOnceAsync(CancellationToken.None));

        // The old boot never delivered a terminal, so its tail cannot be
        // proved delivered: suspicion, reported.
        Assert.Contains(
            "unverified_boot_boundaries=1",
            health.Snapshot().StatusLine(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_corrupt_ledger_is_quarantined_and_reported_not_read_as_absent()
    {
        // cr3-2 round 6: an unreadable-but-present ledger silently became
        // "no memory", disabling loss detection exactly like a fresh
        // install. Detectable corruption is not the accepted
        // whole-root-deletion limit.
        var root = NewRoot("export-corrupt-ledger");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var oldBoot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var first = WriteSegment(options, index: 0, records:
        [
            ChainRecord(oldBoot, sequence: 1),
        ]);

        using var receiver = new FakeHttpDestination();
        var cursorStore = new AuditExportCursorStore(root);
        await using (var service = NewService(
            options,
            receiver,
            cursorStore,
            new AuditExportHealth()))
        {
            Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
        }

        // Cursor lost AND the ledger corrupted, then the old boot (with its
        // undelivered tail) is erased and a new boot starts clean.
        File.Delete(cursorStore.CursorPath);
        var ledgerPath = Path.Combine(root, AuditExportGapStore.FileName);
        Assert.True(File.Exists(ledgerPath), "the durable ledger was never written");
        File.WriteAllText(ledgerPath, "{ this is not valid json");
        File.Delete(first);
        WriteSegment(options, index: 1, records:
        [
            ChainRecord("2a6465d4-6652-4ff7-8630-2ab0c5f6d04c", sequence: 1),
        ]);

        var health = new AuditExportHealth();
        await using var restarted = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        Assert.Equal(1, await restarted.DrainOnceAsync(CancellationToken.None));

        // Losing the proof is itself reportable, and the corrupt artifact is
        // preserved as evidence rather than deleted.
        Assert.Contains(
            "unverified_boot_boundaries",
            health.Snapshot().StatusLine(),
            StringComparison.Ordinal);
        var quarantined = Directory.GetFiles(
            Path.Combine(root, "quarantine"),
            AuditExportGapStore.FileName + "*");
        var artifact = Assert.Single(quarantined);
        Assert.Equal("{ this is not valid json", File.ReadAllText(artifact));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"count":0,"segments":[]}""")]
    [InlineData("")]
    public async Task A_schemaless_ledger_is_corruption_not_an_empty_ledger(string payload)
    {
        // cr3-2 round 7: a structurally valid but schema-less object
        // deserialized to all-defaults and passed as a legitimately empty
        // ledger, silently discarding boot memory. Only a file carrying our
        // own version marker is our ledger.
        var root = NewRoot("export-schemaless-ledger");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var oldBoot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var first = WriteSegment(options, index: 0, records:
        [
            ChainRecord(oldBoot, sequence: 1),
        ]);

        using var receiver = new FakeHttpDestination();
        var cursorStore = new AuditExportCursorStore(root);
        await using (var service = NewService(
            options,
            receiver,
            cursorStore,
            new AuditExportHealth()))
        {
            Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
        }

        File.Delete(cursorStore.CursorPath);
        File.WriteAllText(Path.Combine(root, AuditExportGapStore.FileName), payload);
        File.Delete(first);
        WriteSegment(options, index: 1, records:
        [
            ChainRecord("2a6465d4-6652-4ff7-8630-2ab0c5f6d04c", sequence: 1),
        ]);

        var health = new AuditExportHealth();
        await using var restarted = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        Assert.Equal(1, await restarted.DrainOnceAsync(CancellationToken.None));

        Assert.Contains(
            "unverified_boot_boundaries",
            health.Snapshot().StatusLine(),
            StringComparison.Ordinal);
        var quarantined = Directory.GetFiles(
            Path.Combine(root, "quarantine"),
            AuditExportGapStore.FileName + "*");
        Assert.Single(quarantined);
    }

    [Fact]
    public async Task A_sequence_gap_inside_one_delivery_batch_is_reported()
    {
        // cr3-2 round 8: only the batch's FIRST record was compared, so a
        // jump inside a batch -- 2 and 4 delivered together after 3 was
        // removed -- advanced the cursor with no signal.
        var root = NewRoot("export-intrabatch-gap");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var boot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var segment = WriteSegment(options, index: 0, records:
        [
            ChainRecord(boot, sequence: 1),
        ]);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));

        // Sequences 2 and 4 arrive together; 3 was removed before delivery.
        AppendRecords(segment,
        [
            ChainRecord(boot, sequence: 2),
            ChainRecord(boot, sequence: 4),
        ]);
        Assert.Equal(2, await service.DrainOnceAsync(CancellationToken.None));

        var line = health.Snapshot().StatusLine();
        Assert.Contains("EXPORT_GAPS=1", line, StringComparison.Ordinal);
        Assert.Contains("missing_records=1", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gap_survives_restart_when_only_its_ledger_cannot_be_rewritten()
    {
        // cr3-2 round 9: when the ledger alone could not be replaced, the
        // gap lived only in memory while the cursor still advanced, so a
        // restart silently returned to healthy. The evidence is now parked
        // on the cursor, which is still writable in that scenario.
        var root = NewRoot("export-unwritable-ledger");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var boot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var segment = WriteSegment(options, index: 0, records:
        [
            ChainRecord(boot, sequence: 1),
        ]);

        // A directory in the ledger's place: readable as "absent", never
        // replaceable by the atomic write.
        Directory.CreateDirectory(Path.Combine(root, AuditExportGapStore.FileName));

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        var cursorStore = new AuditExportCursorStore(root);
        await using (var service = NewService(options, receiver, cursorStore, health))
        {
            Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
            // Sequence 3 arrives; 2 was removed before delivery.
            AppendRecords(segment, [ChainRecord(boot, sequence: 3)]);
            Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
            Assert.Contains(
                "EXPORT_GAPS=1",
                health.Snapshot().StatusLine(),
                StringComparison.Ordinal);
        }

        // Restart: the ledger still cannot be written, but the gap must not
        // vanish.
        var restartedHealth = new AuditExportHealth();
        await using var restarted = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            restartedHealth);
        var line = restartedHealth.Snapshot().StatusLine();
        Assert.Contains("EXPORT_GAPS=1", line, StringComparison.Ordinal);
        Assert.Contains("missing_records=1", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parked_gaps_flush_into_the_ledger_once_it_can_be_written()
    {
        // cr3-2 round 10: the round-9 fix parked gap counters on the cursor
        // but never folded them back into the ledger, so a later cursor loss
        // silently restored a healthy status. (The round-9 commit CLAIMED
        // this flush existed; it did not.)
        var root = NewRoot("export-parked-flush");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var boot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        var segment = WriteSegment(options, index: 0, records:
        [
            ChainRecord(boot, sequence: 1),
        ]);
        var ledgerPath = Path.Combine(root, AuditExportGapStore.FileName);
        Directory.CreateDirectory(ledgerPath);

        using var receiver = new FakeHttpDestination();
        var cursorStore = new AuditExportCursorStore(root);
        await using (var service = NewService(
            options,
            receiver,
            cursorStore,
            new AuditExportHealth()))
        {
            Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
            AppendRecords(segment, [ChainRecord(boot, sequence: 3)]);
            Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));
        }

        // The ledger becomes writable; a drain must migrate the parked
        // counters into it.
        Directory.Delete(ledgerPath);
        await using (var recovered = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            new AuditExportHealth()))
        {
            await recovered.DrainOnceAsync(CancellationToken.None);
        }

        // Now lose the cursor entirely: the evidence must survive in the
        // ledger.
        File.Delete(cursorStore.CursorPath);
        var health = new AuditExportHealth();
        await using var restarted = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        Assert.Contains(
            "EXPORT_GAPS=1",
            health.Snapshot().StatusLine(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_pauses_and_reports_when_no_metadata_can_be_written()
    {
        // cr3-2 round 10 also falsified the round-9 residual argument that
        // "if both metadata paths are unwritable the audit root is failing
        // and execution stops first": the spool stays writable, so delivery
        // continued and a restart lost the evidence. Export now pauses and
        // says so -- pausing export never gates execution.
        var root = NewRoot("export-metadata-blocked");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var boot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        WriteSegment(options, index: 0, records:
        [
            ChainRecord(boot, sequence: 1),
            ChainRecord(boot, sequence: 2),
        ]);
        // Both export metadata paths blocked; the journal spool is untouched.
        Directory.CreateDirectory(Path.Combine(root, AuditExportGapStore.FileName));
        Directory.CreateDirectory(Path.Combine(root, AuditExportCursorStore.FileName));

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        await service.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(
            "export.metadata_unwritable",
            health.Snapshot().LastFailureDetail);
        Assert.Contains(
            "retrying",
            health.Snapshot().StatusLine(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_contiguous_multi_record_batch_raises_nothing()
    {
        // The no-alarm half of the same walk: an ordinary batch must not
        // manufacture gaps as it steps through its own records.
        var root = NewRoot("export-intrabatch-clean");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var boot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        WriteSegment(options, index: 0, records:
        [
            ChainRecord(boot, sequence: 1),
            ChainRecord(boot, sequence: 2),
            ChainRecord(boot, sequence: 3),
        ]);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        Assert.Equal(3, await service.DrainOnceAsync(CancellationToken.None));
        Assert.DoesNotContain(
            "EXPORT_GAPS",
            health.Snapshot().StatusLine(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_first_run_from_the_start_of_a_chain_raises_nothing()
    {
        // The no-alarm half: an ordinary first run whose oldest surviving
        // record IS sequence 1 has lost nothing.
        var root = NewRoot("export-first-run-clean");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var boot = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        WriteSegment(options, index: 0, records:
        [
            ChainRecord(boot, sequence: 1),
            ChainRecord(boot, sequence: 2),
        ]);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        Assert.Equal(2, await service.DrainOnceAsync(CancellationToken.None));
        Assert.DoesNotContain(
            "EXPORT_GAPS",
            health.Snapshot().StatusLine(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clean_boot_boundary_at_sequence_one_raises_nothing()
    {
        // Each supervisor boot owns its own chain from 1. When the previous
        // boot ended with its lifecycle terminal and the next starts at 1,
        // nothing is outstanding and nothing is lost.
        var root = NewRoot("export-clean-boot");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var first = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        WriteSegment(options, index: 0, records:
        [
            ChainRecord(first, sequence: 1),
            ChainRecord(first, sequence: 2, eventType: "server.stopped"),
        ]);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        Assert.Equal(2, await service.DrainOnceAsync(CancellationToken.None));

        WriteSegment(options, index: 1, records:
        [
            ChainRecord("2a6465d4-6652-4ff7-8630-2ab0c5f6d04c", sequence: 1),
        ]);
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));

        var line = health.Snapshot().StatusLine();
        Assert.DoesNotContain("EXPORT_GAPS", line, StringComparison.Ordinal);
        Assert.DoesNotContain("unverified_boot_boundaries", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_boots_deleted_prefix_is_reported_as_a_gap()
    {
        // cr3-2 round 3: a boot change skipped comparison entirely, so
        // records 1..N-1 of the NEW boot could be deleted before delivery and
        // never be noticed. A new chain must start at 1.
        var root = NewRoot("export-newboot-prefix");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var first = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        WriteSegment(options, index: 0, records:
        [
            ChainRecord(first, sequence: 1),
            ChainRecord(first, sequence: 2, eventType: "server.stopped"),
        ]);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        Assert.Equal(2, await service.DrainOnceAsync(CancellationToken.None));

        // The new boot's first three records were removed before delivery.
        WriteSegment(options, index: 1, records:
        [
            ChainRecord("2a6465d4-6652-4ff7-8630-2ab0c5f6d04c", sequence: 4),
        ]);
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));

        var line = health.Snapshot().StatusLine();
        Assert.Contains("EXPORT_GAPS=1", line, StringComparison.Ordinal);
        Assert.Contains("missing_records=3", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unterminated_boot_followed_by_a_new_one_is_flagged_unverified()
    {
        // The other half of cr3-2 round 3: the OLD boot's tail cannot be
        // proved either way from sequences alone. Reported as suspicion —
        // never counted as proved loss.
        var root = NewRoot("export-unverified-boundary");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        var first = "d99ba8e8-25c5-4bfb-9c39-364407e4d96d";
        WriteSegment(options, index: 0, records:
        [
            ChainRecord(first, sequence: 1),
        ]);

        using var receiver = new FakeHttpDestination();
        var health = new AuditExportHealth();
        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));

        // No terminal was ever delivered for that boot, and the next boot —
        // whose lineage attests it (cr4-4: the boundary judgment reads
        // attestation, since per-boot traversal has no crossing moment) —
        // begins cleanly at 1.
        WriteSegment(options, index: 1, records:
        [
            ChainRecord(
                "2a6465d4-6652-4ff7-8630-2ab0c5f6d04c",
                sequence: 1,
                previousBootId: first),
        ]);
        Assert.Equal(1, await service.DrainOnceAsync(CancellationToken.None));

        var line = health.Snapshot().StatusLine();
        Assert.Contains("unverified_boot_boundaries=1", line, StringComparison.Ordinal);
        // Suspicion must not masquerade as proof.
        Assert.DoesNotContain("EXPORT_GAPS", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transient_408_is_retried_instead_of_skipping_the_batch()
    {
        // cr3-5: 408 fell through to Permanent, which skipped up to a whole
        // batch of audit records.
        using var receiver = new FakeHttpDestination
        {
            ResponseStatus = HttpStatusCode.RequestTimeout,
        };
        var settings = new AuditExportSettings(
            AuditDestinationKind.OtlpHttp,
            receiver.BaseUri,
            null);
        using var destination = new HttpAuditDestination(settings);

        var result = await destination.DeliverAsync(
            ["""{"event_type":"call.completed"}"""],
            CancellationToken.None);

        Assert.Equal(AuditDeliveryDisposition.Retryable, result.Disposition);
        Assert.Equal("export.http_408", result.DetailCode);
    }

    [Fact]
    public async Task A_refused_batch_isolates_the_poison_record_and_keeps_the_rest()
    {
        // cr3-5: a permanent refusal advanced the cursor past the entire
        // batch, discarding custody of every record travelling with the one
        // the destination actually refused.
        var root = NewRoot("export-isolate");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        WriteSegment(options, index: 0, records:
        [
            """{"event_type":"server.started","event_id":"good-1"}""",
            """{"event_type":"call.completed","event_id":"poison"}""",
            """{"event_type":"call.completed","event_id":"good-2"}""",
        ]);

        using var receiver = new FakeHttpDestination
        {
            // The batch and the poison record are refused; the two healthy
            // records are accepted when delivered on their own.
            RefusePredicate = body => body.Contains("poison", StringComparison.Ordinal),
            RefusalStatus = HttpStatusCode.BadRequest,
        };
        var health = new AuditExportHealth();
        await using var service = NewService(
            options,
            receiver,
            new AuditExportCursorStore(root),
            health);

        var delivered = await service.DrainOnceAsync(CancellationToken.None);
        Assert.Equal(2, delivered);

        var deliveredIds = receiver.Requests
            .Where(request => !request.Body.Contains("poison", StringComparison.Ordinal))
            .SelectMany(request => new[] { request.Body })
            .ToArray();
        Assert.Contains(deliveredIds, body => body.Contains("good-1", StringComparison.Ordinal));
        Assert.Contains(deliveredIds, body => body.Contains("good-2", StringComparison.Ordinal));
    }

    [Fact]
    public void Export_health_reports_a_missing_destination_as_local_journal_only()
    {
        var health = new AuditExportHealth();
        Assert.Contains(
            "not configured (local journal only)",
            health.Snapshot().StatusLine(),
            StringComparison.Ordinal);
    }

    private static AuditExportService NewService(
        AuditOptions options,
        FakeHttpDestination receiver,
        AuditExportCursorStore cursorStore,
        AuditExportHealth health,
        Func<AuditJournal?>? liveJournalSource = null)
    {
        var settings = new AuditExportSettings(
            AuditDestinationKind.OtlpHttp,
            receiver.BaseUri,
            null);
        return new AuditExportService(
            options,
            new HttpAuditDestination(settings),
            cursorStore,
            health,
            liveJournalSource: liveJournalSource);
    }

    private static AuditExportBootPosition PositionFor(
        AuditExportCursor cursor,
        string segmentPath)
    {
        Assert.True(AuditSpoolSegmentIdentity.TryParse(
            Path.GetFileName(segmentPath),
            out var identity));
        var position = cursor.For(identity.SupervisorBootId);
        Assert.NotNull(position);
        return position!;
    }

    private static int AppendJournalEvents(AuditJournal journal, int count)
    {
        for (var index = 0; index < count; index++)
        {
            Assert.True(journal.TryReserve(1, out var reservation, out _));
            journal.Append(reservation!, new AuditEventInput
            {
                EventType = "call.completed",
                Session = new AuditSession { Name = "default", Generation = 0, BindingKind = "default" },
                Actor = new AuditActor { AttributionStrength = "unknown" },
                Correlation = new AuditCorrelation(),
                Request = new AuditRequest(),
                Routing = new AuditRouting(),
                Outcome = new AuditOutcome { State = "completed", TerminationCertainty = "not_applicable" },
                Coverage = new AuditCoverage
                {
                    PtkRequest = true,
                    RootProcessObserved = "not_applicable",
                    DescendantsObserved = "not_applicable",
                    RemoteEffectObserved = "not_applicable",
                },
                Audit = new AuditEventHealth { ProtectionMode = "local-only", HealthState = "healthy" },
            });
            reservation!.Release();
        }
        return count;
    }

    /// <summary>A canonical journal line carrying the chain position the
    /// exporter uses to prove loss: per-boot contiguous sequence, plus the
    /// boot-lineage attestation the boundary judgment reads (cr4-4).</summary>
    private static string ChainRecord(
        string supervisorBootId,
        long sequence,
        string eventType = "call.completed",
        string? previousBootId = null) =>
        "{\"schema_version\":\"ptk.audit/2\",\"event_id\":\"019f5ee1-2384-7eac-8f88-2eb4e7ec5e" +
        sequence.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) +
        "\",\"event_type\":\"" + eventType + "\",\"sequence\":" +
        sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"producer\":{\"host_id\":\"92874c03-05a7-4aa6-8094-b2e87cad5696\"," +
        "\"supervisor_boot_id\":\"" + supervisorBootId + "\"," +
        "\"previous_supervisor_boot_id\":" +
        (previousBootId is null ? "null" : "\"" + previousBootId + "\"") +
        ",\"worker_boot_id\":null," +
        "\"pid\":32890,\"version\":\"1.0.0.0\",\"binary_digest\":null}}";

    private static string WriteSegment(
        AuditOptions options,
        int index,
        string[] records,
        string? bootId = null,
        DateTime? createdUtc = null)
    {
        // A segment's file name carries its boot id, and the walk rightly
        // refuses records that contradict their segment's boot (cr4-4) — so
        // an unspecified boot is derived from the records, as production
        // segments are by construction.
        var boot = bootId is not null
            ? Guid.Parse(bootId)
            : EmbeddedBootId(records) ?? Guid.NewGuid();
        var identity = AuditSpoolSegmentIdentity.Create(boot, index);
        var path = Path.Combine(options.SpoolDirectory, identity.FileName);
        File.WriteAllText(path, string.Concat(records.Select(record => record + "\n")));
        if (createdUtc is not null) File.SetCreationTimeUtc(path, createdUtc.Value);
        return path;
    }

    private static Guid? EmbeddedBootId(string[] records)
    {
        foreach (var record in records)
        {
            try
            {
                using var document = JsonDocument.Parse(record);
                if (document.RootElement.TryGetProperty("producer", out var producer) &&
                    producer.ValueKind == JsonValueKind.Object &&
                    producer.TryGetProperty("supervisor_boot_id", out var boot) &&
                    boot.ValueKind == JsonValueKind.String &&
                    Guid.TryParseExact(boot.GetString(), "D", out var parsed))
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // Undecorated records fall through to a random boot.
            }
        }
        return null;
    }

    private static void AppendRecords(string segmentPath, string[] records) =>
        File.AppendAllText(
            segmentPath,
            string.Concat(records.Select(record => record + "\n")));

    private static void WriteSettings(
        string root,
        string kind,
        string endpoint,
        string credential)
    {
        var path = Path.Combine(root, AuditExportSettings.FileName);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                kind,
                endpoint,
                credential,
            }));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, SecureAuditStorage.OwnerFileMode);
    }

    private string NewRoot(string label)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            $"test-{label}-{Guid.NewGuid():N}");
        _roots.Add(root);
        return SecureAuditStorage.PrepareRoot(root);
    }

    /// <summary>Minimal in-process HTTP endpoint standing in for Splunk, a
    /// collector, or the PTK receiver — all three are reached identically.</summary>
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

        /// <summary>Refuses only the bodies this predicate matches, so a
        /// poison record can be isolated from healthy ones.</summary>
        internal Func<string, bool>? RefusePredicate { get; set; }

        internal HttpStatusCode RefusalStatus { get; set; } = HttpStatusCode.BadRequest;

        internal List<ReceivedRequest> Requests { get; } = [];

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
                lock (Requests)
                {
                    Requests.Add(new ReceivedRequest(
                        context.Request.Url?.AbsolutePath ?? string.Empty,
                        context.Request.Headers["Authorization"],
                        body));
                }
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

        internal sealed record ReceivedRequest(
            string Path,
            string? Authorization,
            string Body);
    }
}
