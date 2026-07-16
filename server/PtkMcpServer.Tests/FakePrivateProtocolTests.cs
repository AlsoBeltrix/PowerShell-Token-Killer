using System.Buffers;
using System.Text;
using System.Text.Json;
using PtkResilienceTestFixture;

namespace PtkMcpServer.Tests;

public sealed class FakePrivateProtocolTests
{
    [Fact]
    public async Task Fixture_v1_codec_is_strict_bounded_directional_and_preserves_validated_raw_bytes()
    {
        Assert.Equal(1, FakePrivateProtocol.Version);
        Assert.Equal(1_048_576, FakePrivateProtocol.MaximumEncodedFrameBytes);
        Assert.Equal(32, FakePrivateProtocol.MaximumJsonDepth);

        var envelope = Hello();
        var encoded = FakePrivateProtocol.Encode(envelope, FakePrivatePeer.Host);
        var decoded = FakePrivateProtocol.Decode(encoded, FakePrivatePeer.Host);
        Assert.Equal(FakePrivateMessageKind.Hello, decoded.Kind);
        Assert.Equal(envelope.GuardianBootId, decoded.GuardianBootId);
        Assert.Equal(envelope.HostBootId, decoded.HostBootId);
        Assert.Equal(envelope.HostGeneration, decoded.HostGeneration);

        var exact = Encoding.UTF8.GetString(encoded);
        Assert.StartsWith(
            "{\"protocol_version\":1,\"kind\":\"hello\",\"guardian_boot_id\":",
            exact,
            StringComparison.Ordinal);
        Assert.DoesNotContain('\r', exact);
        Assert.DoesNotContain('\n', exact);

        AssertProtocolFailure("wrong_direction", () =>
            FakePrivateProtocol.Decode(encoded, FakePrivatePeer.Guardian));
        AssertProtocolFailure("bom_forbidden", () =>
            FakePrivateProtocol.Decode(
                new byte[] { 0xef, 0xbb, 0xbf }.Concat(encoded).ToArray(),
                FakePrivatePeer.Host));
        AssertProtocolFailure("invalid_utf8", () =>
            FakePrivateProtocol.Decode(new byte[] { 0xff }, FakePrivatePeer.Host));
        AssertProtocolFailure("invalid_framing", () =>
            FakePrivateProtocol.Decode(encoded.Append((byte)'\r').ToArray(), FakePrivatePeer.Host));
        AssertProtocolFailure("frame_too_large", () =>
            FakePrivateProtocol.Decode(
                new byte[FakePrivateProtocol.MaximumEncodedFrameBytes + 1],
                FakePrivatePeer.Host));

        var duplicate = exact.Replace(
            "\"host_pid\":4242",
            "\"host_pid\":4242,\"host_pid\":4242",
            StringComparison.Ordinal);
        Assert.NotEqual(exact, duplicate);
        AssertProtocolFailure("duplicate_field", () =>
            FakePrivateProtocol.Decode(Encoding.UTF8.GetBytes(duplicate), FakePrivatePeer.Host));

        var outOfOrder = exact.Replace(
            "{\"protocol_version\":1,\"kind\":\"hello\"",
            "{\"kind\":\"hello\",\"protocol_version\":1",
            StringComparison.Ordinal);
        Assert.NotEqual(exact, outOfOrder);
        AssertProtocolFailure("property_order", () =>
            FakePrivateProtocol.Decode(Encoding.UTF8.GetBytes(outOfOrder), FakePrivatePeer.Host));

        var unknownVersion = exact.Replace(
            "\"protocol_version\":1",
            "\"protocol_version\":2",
            StringComparison.Ordinal);
        AssertProtocolFailure("unknown_version", () =>
            FakePrivateProtocol.Decode(
                Encoding.UTF8.GetBytes(unknownVersion),
                FakePrivatePeer.Host));

        var unknownKind = exact.Replace(
            "\"kind\":\"hello\"",
            "\"kind\":\"unknown\"",
            StringComparison.Ordinal);
        AssertProtocolFailure("unknown_kind", () =>
            FakePrivateProtocol.Decode(
                Encoding.UTF8.GetBytes(unknownKind),
                FakePrivatePeer.Host));

        await using (var truncated = new MemoryStream(encoded, writable: false))
        {
            var failure = await Assert.ThrowsAsync<FakePrivateProtocolException>(async () =>
                await FakePrivateProtocol.ReadAsync(
                    truncated,
                    FakePrivatePeer.Host));
            Assert.Equal("truncated_frame", failure.DetailCode);
        }

        await using var output = new MemoryStream();
        var writer = new FakePrivateProtocolWriter(output, FakePrivatePeer.Host);
        await writer.WriteRawAsync(encoded);
        Assert.Equal([.. encoded, (byte)'\n'], output.ToArray());
    }

    [Fact]
    public void Encode_rejects_payload_beyond_the_frozen_json_depth()
    {
        const int nesting = FakePrivateProtocol.MaximumJsonDepth + 8;
        var deepJson = "{\"value\":" + new string('[', nesting) + "0" +
            new string(']', nesting) + "}";
        using var document = JsonDocument.Parse(
            deepJson,
            new JsonDocumentOptions { MaxDepth = nesting + 8 });
        var envelope = FakePrivateProtocol.Create(
            FakePrivateMessageKind.Response,
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            1,
            ("request_id", 1),
            ("status", "ok"),
            ("payload", document.RootElement),
            ("error", null));

        AssertProtocolFailure("invalid_json", () =>
            FakePrivateProtocol.Encode(envelope, FakePrivatePeer.Host));
    }

    [Fact]
    public void Fake_manifest_vector_matches_the_frozen_recovery_manifest_schema()
    {
        var guardianBootId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        const long generation = 42;
        var encoded = FakePrivateHostConnection.BuildManifestVector(guardianBootId, generation);
        var secondEncoding = FakePrivateHostConnection.BuildManifestVector(
            guardianBootId,
            generation);
        Assert.Equal(encoded, secondEncoding);
        Assert.NotEqual(0xef, encoded[0]);
        Assert.DoesNotContain((byte)'\r', encoded);
        Assert.DoesNotContain((byte)'\n', encoded);

        using var manifest = JsonDocument.Parse(encoded);
        using var schema = JsonDocument.Parse(File.ReadAllBytes(ContractPath(
            "recovery-manifest.schema.json")));
        AssertPropertyOrder(
            schema.RootElement.GetProperty("x-ptk-property-order"),
            manifest.RootElement);

        var root = manifest.RootElement;
        Assert.Equal("ptk.recovery-manifest/1", root.GetProperty("schema_version").GetString());
        Assert.Equal(guardianBootId, root.GetProperty("guardian_boot_id").GetGuid());
        Assert.Equal(generation, root.GetProperty("host_generation").GetInt64());
        Assert.Equal(generation, root.GetProperty("host_generation_high_watermark").GetInt64());
        Assert.Empty(root.GetProperty("templates").EnumerateArray());

        var binding = Assert.Single(root.GetProperty("bindings").EnumerateArray());
        AssertPropertyOrder(
            schema.RootElement.GetProperty("$defs").GetProperty("binding")
                .GetProperty("x-ptk-property-order"),
            binding);
        Assert.Equal("default", binding.GetProperty("alias").GetString());
        Assert.Equal("default", binding.GetProperty("binding_kind").GetString());
        Assert.Equal(JsonValueKind.Null, binding.GetProperty("template_name").ValueKind);
        Assert.Equal(JsonValueKind.Null, binding.GetProperty("template_digest").ValueKind);
        Assert.Equal(JsonValueKind.Null, binding.GetProperty("bootstrap_digest").ValueKind);
        Assert.False(binding.GetProperty("allow_cold_background").GetBoolean());
        Assert.Equal("ready", binding.GetProperty("desired_state").GetString());
        Assert.Equal(0, binding.GetProperty("transition_version").GetInt64());
        Assert.Equal(
            FakePrivateFixtureIdentity.DefaultBindingSha256,
            binding.GetProperty("binding_digest").GetString());

        var highWatermark = Assert.Single(
            root.GetProperty("worker_generation_high_watermarks").EnumerateArray());
        AssertPropertyOrder(
            schema.RootElement.GetProperty("$defs")
                .GetProperty("worker_generation_high_watermark")
                .GetProperty("x-ptk-property-order"),
            highWatermark);
        Assert.Equal("default", highWatermark.GetProperty("alias").GetString());
        Assert.Equal(0, highWatermark.GetProperty("generation").GetInt64());
    }

    [Fact]
    public void Every_fixture_envelope_kind_has_one_exact_direction_and_round_trips()
    {
        foreach (var (envelope, sender) in AllEnvelopeKinds())
        {
            var encoded = FakePrivateProtocol.Encode(envelope, sender);
            var decoded = FakePrivateProtocol.Decode(encoded, sender);
            Assert.Equal(envelope.Kind, decoded.Kind);
            Assert.Equal(envelope.GuardianBootId, decoded.GuardianBootId);
            Assert.Equal(envelope.HostBootId, decoded.HostBootId);
            Assert.Equal(envelope.HostGeneration, decoded.HostGeneration);

            var wrongSender = sender == FakePrivatePeer.Guardian
                ? FakePrivatePeer.Host
                : FakePrivatePeer.Guardian;
            AssertProtocolFailure("wrong_direction", () =>
                FakePrivateProtocol.Decode(encoded, wrongSender));
        }
    }

    [Fact]
    public async Task Incremental_reader_preserves_fragmented_and_coalesced_frames_and_clears_pool_buffers()
    {
        var first = FakePrivateProtocol.Encode(Hello(), FakePrivatePeer.Host);
        var second = FakePrivateProtocol.Encode(Hello(), FakePrivatePeer.Host);
        var combined = first.Concat([(byte)'\n']).Concat(second).Concat([(byte)'\n']).ToArray();

        var coalescedPool = new TrackingArrayPool();
        await using (var coalesced = new MemoryStream(combined, writable: false))
        {
            var reader = new FakePrivateProtocolReader(
                coalesced,
                FakePrivatePeer.Host,
                coalescedPool);
            Assert.Equal(FakePrivateMessageKind.Hello, (await reader.ReadAsync())!.Kind);
            Assert.Equal(FakePrivateMessageKind.Hello, (await reader.ReadAsync())!.Kind);
            Assert.Null(await reader.ReadAsync());
        }
        Assert.Equal(3, coalescedPool.ReturnCount);
        Assert.True(coalescedPool.EveryReturnRequestedClearing);

        var fragmentedPool = new TrackingArrayPool();
        await using (var fragmented = new FragmentingReadStream(combined, maximumChunkBytes: 3))
        {
            var reader = new FakePrivateProtocolReader(
                fragmented,
                FakePrivatePeer.Host,
                fragmentedPool);
            Assert.Equal(FakePrivateMessageKind.Hello, (await reader.ReadAsync())!.Kind);
            Assert.Equal(FakePrivateMessageKind.Hello, (await reader.ReadAsync())!.Kind);
            Assert.Null(await reader.ReadAsync());
        }
        Assert.Equal(3, fragmentedPool.ReturnCount);
        Assert.True(fragmentedPool.EveryReturnRequestedClearing);
    }

    [Fact]
    public async Task Writer_serializes_concurrent_frames_and_latches_ambiguous_failure()
    {
        var interleaving = new InterleavingWriteStream();
        var writer = new FakePrivateProtocolWriter(interleaving, FakePrivatePeer.Host);
        var writes = Enumerable.Range(1, 16)
            .Select(requestId => writer.WriteAsync(Response(requestId)).AsTask())
            .ToArray();
        await Task.WhenAll(writes);

        var lines = Encoding.UTF8.GetString(interleaving.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(16, lines.Length);
        Assert.Equal(
            Enumerable.Range(1, 16).Select(value => (long)value),
            lines.Select(line => FakePrivateProtocol.Decode(
                    Encoding.UTF8.GetBytes(line),
                    FakePrivatePeer.Host)
                .Value("request_id").GetInt64())
                .Order());

        var failing = new FailingWriteStream();
        var faultingWriter = new FakePrivateProtocolWriter(failing, FakePrivatePeer.Host);
        await Assert.ThrowsAsync<IOException>(async () =>
            await faultingWriter.WriteAsync(Response(1)));
        var latched = await Assert.ThrowsAsync<FakePrivateProtocolException>(async () =>
            await faultingWriter.WriteAsync(Response(2)));
        Assert.Equal("writer_faulted", latched.DetailCode);
        Assert.Equal(1, failing.WriteCount);
    }

    private static FakePrivateEnvelope Hello() => FakePrivateProtocol.Create(
        FakePrivateMessageKind.Hello,
        Guid.Parse("11111111-1111-4111-8111-111111111111"),
        Guid.Parse("22222222-2222-4222-8222-222222222222"),
        1,
        ("host_pid", 4242),
        ("host_executable_sha256", new string('1', 64)),
        ("host_build_sha256", new string('2', 64)),
        ("public_contract_sha256", new string('3', 64)),
        ("configuration_sha256", new string('4', 64)),
        ("request_channel_owned", true),
        ("event_channel_owned", true));

    private static FakePrivateEnvelope Response(long requestId) => FakePrivateProtocol.Create(
        FakePrivateMessageKind.Response,
        Guid.Parse("11111111-1111-4111-8111-111111111111"),
        Guid.Parse("22222222-2222-4222-8222-222222222222"),
        1,
        ("request_id", requestId),
        ("status", "ok"),
        ("payload", JsonSerializer.SerializeToElement(new Dictionary<string, object?>())),
        ("error", null));

    private static (FakePrivateEnvelope Envelope, FakePrivatePeer Sender)[] AllEnvelopeKinds()
    {
        var guardian = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var host = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var manifest = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var digest = new string('a', 64);
        var empty = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        var eventPayload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["binding_digest"] = digest,
            ["startup_deadline_unix_time_milliseconds"] = 1L,
        });

        return
        [
            (Hello(), FakePrivatePeer.Host),
            (FakePrivateProtocol.Create(
                FakePrivateMessageKind.Initialize,
                guardian,
                host,
                1,
                ("request_id", 1L),
                ("guardian_protocol_version", 1),
                ("host_protocol_version", 1),
                ("host_executable_sha256", digest),
                ("host_build_sha256", digest),
                ("public_contract_sha256", digest),
                ("configuration_sha256", digest),
                ("package_manifest_sha256", digest),
                ("maximum_manifest_bytes", 25_165_824),
                ("maximum_manifest_chunk_raw_bytes", 524_288),
                ("maximum_aliases", 128),
                ("maximum_templates", 128)), FakePrivatePeer.Guardian),
            (FakePrivateProtocol.Create(
                FakePrivateMessageKind.Ready,
                guardian,
                host,
                1,
                ("initialize_request_id", 1L),
                ("manifest_id", manifest),
                ("manifest_sha256", digest),
                ("host_pid", 4242)), FakePrivatePeer.Host),
            (FakePrivateProtocol.Create(
                FakePrivateMessageKind.Request,
                guardian,
                host,
                1,
                ("request_id", 2L),
                ("method", "operation"),
                ("deadline_unix_time_milliseconds", null),
                ("session_alias", null),
                ("session_transition_version", null),
                ("worker_boot_id", null),
                ("worker_generation", null),
                ("plan_id", null),
                ("operation_id", null),
                ("payload", empty)), FakePrivatePeer.Guardian),
            (FakePrivateProtocol.Create(
                FakePrivateMessageKind.Cancel,
                guardian,
                host,
                1,
                ("request_id", 3L),
                ("target_request_id", 2L),
                ("reason", "caller_canceled")), FakePrivatePeer.Guardian),
            (FakePrivateProtocol.Create(
                FakePrivateMessageKind.Event,
                guardian,
                host,
                1,
                ("event_sequence", 1L),
                ("event_type", "worker_create_capability_requested"),
                ("request_id", null),
                ("session_alias", "default"),
                ("session_transition_version", 1L),
                ("worker_boot_id", null),
                ("worker_generation", null),
                ("plan_id", null),
                ("operation_id", null),
                ("payload", eventPayload)), FakePrivatePeer.Host),
            (Response(4), FakePrivatePeer.Host),
            (FakePrivateProtocol.Create(
                FakePrivateMessageKind.Shutdown,
                guardian,
                host,
                1,
                ("request_id", 5L),
                ("deadline_unix_time_milliseconds", 1L),
                ("reason", "guardian_shutdown")), FakePrivatePeer.Guardian),
        ];
    }

    private static void AssertProtocolFailure(string detailCode, Action action)
    {
        var failure = Assert.Throws<FakePrivateProtocolException>(action);
        Assert.Equal(detailCode, failure.DetailCode);
    }

    private static void AssertPropertyOrder(JsonElement expectedOrder, JsonElement actual)
    {
        Assert.Equal(
            expectedOrder.EnumerateArray().Select(value => value.GetString()),
            actual.EnumerateObject().Select(property => property.Name));
    }

    private static string ContractPath(string fileName)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "server",
                "Contracts",
                "ResilienceR0",
                fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate frozen R0 contract '{fileName}'.");
    }

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        internal int ReturnCount { get; private set; }
        internal bool EveryReturnRequestedClearing { get; private set; } = true;

        public override byte[] Rent(int minimumLength) => new byte[minimumLength];

        public override void Return(byte[] array, bool clearArray = false)
        {
            ReturnCount++;
            EveryReturnRequestedClearing &= clearArray;
            if (clearArray) Array.Clear(array);
        }
    }

    private sealed class FragmentingReadStream(byte[] bytes, int maximumChunkBytes) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = Math.Min(Math.Min(count, maximumChunkBytes), bytes.Length - _offset);
            if (length <= 0) return 0;
            bytes.AsSpan(_offset, length).CopyTo(buffer.AsSpan(offset, length));
            _offset += length;
            return length;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(
                Math.Min(buffer.Length, maximumChunkBytes),
                bytes.Length - _offset);
            if (length <= 0) return ValueTask.FromResult(0);
            bytes.AsMemory(_offset, length).CopyTo(buffer);
            _offset += length;
            return ValueTask.FromResult(length);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class InterleavingWriteStream : Stream
    {
        private readonly MemoryStream _buffer = new();
        private readonly object _sync = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length { get { lock (_sync) return _buffer.Length; } }
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var midpoint = Math.Max(1, buffer.Length / 2);
            lock (_sync) _buffer.Write(buffer.Span[..midpoint]);
            await Task.Yield();
            lock (_sync) _buffer.Write(buffer.Span[midpoint..]);
        }

        internal byte[] ToArray()
        {
            lock (_sync) return _buffer.ToArray();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_sync) _buffer.Write(buffer, offset, count);
        }
    }

    private sealed class FailingWriteStream : Stream
    {
        internal int WriteCount { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return ValueTask.FromException(new IOException("Injected writer failure."));
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
