using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PtkMcpServer.Audit.OtlpWire;
using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Ingest;
using PtkSiemReceiver.Security;
using PtkSiemReceiver.Storage;

namespace PtkSiemReceiver.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SiemReceiverProcessCollection
{
    public const string Name = "siem receiver process";
}

internal static class SiemTestFileSystem
{
    internal static string CreateProtectedRoot(string prefix)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            $".{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _ = SiemProtectedPath.ProtectCreatedDirectory(root);
        return root;
    }

    internal static string WriteProtectedText(string root, string name, string text)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, text);
        _ = SiemProtectedPath.ProtectCreatedFile(path);
        return path;
    }

    internal static string WriteProtectedBytes(string root, string name, byte[] bytes)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        _ = SiemProtectedPath.ProtectCreatedFile(path);
        return path;
    }
}

internal sealed class TestCertificateAuthority : IDisposable
{
    private readonly X509Certificate2 _root;

    internal TestCertificateAuthority()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN=ptk-siem-test-root-{Guid.NewGuid():N}"),
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        _root = request.CreateSelfSigned(now.AddDays(-10), now.AddDays(30));
    }

    internal X509Certificate2 Root =>
        X509CertificateLoader.LoadCertificate(_root.RawData);

    internal X509Certificate2 IssueServer() =>
        Issue(
            "ptk-siem-test-server",
            "1.3.6.1.5.5.7.3.1",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(2),
            addLoopbackSan: true);

    internal X509Certificate2 IssueClient(
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        string eku = "1.3.6.1.5.5.7.3.2") =>
        Issue(
            "ptk-siem-test-client",
            eku,
            notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(1),
            addLoopbackSan: false);

    public void Dispose() => _root.Dispose();

    private X509Certificate2 Issue(
        string commonName,
        string eku,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        bool addLoopbackSan)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={commonName}-{Guid.NewGuid():N}"),
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(eku) },
            true));
        if (addLoopbackSan)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var issued = request.Create(
            _root,
            notBefore,
            notAfter,
            RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(key);
        var pkcs12 = withKey.Export(X509ContentType.Pkcs12, string.Empty);
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pkcs12,
                string.Empty,
                X509KeyStorageFlags.DefaultKeySet | X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }
}

internal sealed class SiemReceiverTestHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly string _root;
    private readonly string _witnessRoot;
    private readonly string? _anchorRoot;

    private readonly bool _preserveRootOnDispose;
    private readonly bool _preserveWitnessOnDispose;
    private readonly bool _preserveAnchorOnDispose;

    private SiemReceiverTestHost(
        WebApplication application,
        string root,
        Uri endpoint,
        Uri operatorEndpoint,
        string databasePath,
        bool preserveRootOnDispose,
        string witnessRoot,
        bool preserveWitnessOnDispose,
        string? anchorRoot,
        bool preserveAnchorOnDispose)
    {
        _application = application;
        _root = root;
        Endpoint = endpoint;
        OperatorEndpoint = operatorEndpoint;
        DatabasePath = databasePath;
        _preserveRootOnDispose = preserveRootOnDispose;
        _witnessRoot = witnessRoot;
        _preserveWitnessOnDispose = preserveWitnessOnDispose;
        _anchorRoot = anchorRoot;
        _preserveAnchorOnDispose = preserveAnchorOnDispose;
    }

    internal string Root => _root;

    internal string WitnessRoot => _witnessRoot;

    internal string? AnchorRoot => _anchorRoot;

    internal CustodyWitness CustodyWitness =>
        _application.Services.GetRequiredService<CustodyWitness>();

    internal Uri Endpoint { get; }

    /// <summary>Base URI of the operator query surface (S5): plain HTTP on
    /// loopback in tests, exactly the no-certificate configuration.</summary>
    internal Uri OperatorEndpoint { get; }

    internal const string OperatorToken = "tttttttttttttttttttttttttttttttt";

    internal string DatabasePath { get; }

    internal static async Task<SiemReceiverTestHost> StartAsync(
        X509Certificate2 serverCertificate,
        IReadOnlyList<X509Certificate2> trustedClientAuthorities,
        IIngestCommitter? committer = null,
        X509RevocationMode revocationMode = X509RevocationMode.NoCheck,
        int maximumRequestBytes = 1024 * 1024,
        int maximumConcurrentRequests = SiemReceiverConfigurationLoader.DefaultMaxConcurrentRequests,
        TimeProvider? timeProvider = null,
        ISqliteIngestFaultInjector? storageFaultInjector = null,
        string? ingestToken = null,
        string? existingRoot = null,
        bool preserveRootOnDispose = false,
        string? existingWitnessRoot = null,
        bool preserveWitnessOnDispose = false,
        string? existingAnchorRoot = null,
        bool preserveAnchorOnDispose = false,
        IReadOnlyList<AlertRule>? alertRules = null,
        string? alertWebhookUrl = null,
        bool alertEvaluationHoldForTests = false,
        SiemReceiverTestHostFailureHooks? failureHooks = null)
    {
        // A restart test hands the previous host's root back in: certificate
        // material and the database are reused as a real restart would.
        var root = existingRoot ?? SiemTestFileSystem.CreateProtectedRoot("ptk-siem-host");
        var witnessRoot = existingWitnessRoot ??
            SiemTestFileSystem.CreateProtectedRoot("ptk-siem-witness-host");
        var certificatePath = Path.Combine(root, "server-cert.pem");
        var keyPath = Path.Combine(root, "server-key.pem");
        var authorityPath = Path.Combine(root, "client-roots.pem");
        if (existingRoot is null)
        {
            await File.WriteAllTextAsync(certificatePath, serverCertificate.ExportCertificatePem());
            _ = SiemProtectedPath.ProtectCreatedFile(certificatePath);
            using (var key = serverCertificate.GetRSAPrivateKey() ??
                             throw new InvalidOperationException("The test server certificate has no RSA key."))
            {
                await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
            }
            _ = SiemProtectedPath.ProtectCreatedFile(keyPath);
            await File.WriteAllTextAsync(
                authorityPath,
                string.Join(
                    Environment.NewLine,
                    trustedClientAuthorities.Select(certificate => certificate.ExportCertificatePem())));
            _ = SiemProtectedPath.ProtectCreatedFile(authorityPath);
        }

        var databasePath = Path.Combine(root, "siem.db");
        var options = new SiemReceiverOptions(
            IPAddress.Loopback,
            0,
            certificatePath,
            keyPath,
            [authorityPath],
            revocationMode,
            maximumRequestBytes,
            maximumConcurrentRequests,
            IPAddress.Loopback,
            0,
            OperatorToken,
            null,
            null,
            databasePath,
            null,
            null,
            ingestToken: ingestToken,
            alertRules: alertRules,
            alertWebhookUrl: alertWebhookUrl,
            custodyWitness: new CustodyWitnessOptions(
                witnessRoot,
                CheckpointIntervalSeconds: 3_600,
                AnchorDirectoryPath: existingAnchorRoot));

        WebApplication? application = null;
        try
        {
            application = ReceiverApplication.Build(
                options,
                committer: committer,
                timeProvider: timeProvider,
                storageFaultInjector: storageFaultInjector,
                alertEvaluationHoldForTests: alertEvaluationHoldForTests);
            if (failureHooks is null)
                await application.StartAsync();
            else
                await failureHooks.StartApplication(application);
            var server = application.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ??
                            throw new InvalidOperationException("Kestrel did not publish an address.");
            // Two listeners since S5: ingest is the HTTPS one, the operator
            // surface is plain HTTP in this no-certificate configuration.
            var ingestAddress = Assert.Single(
                addresses, address => address.StartsWith("https://", StringComparison.Ordinal));
            var operatorAddress = Assert.Single(
                addresses, address => address.StartsWith("http://", StringComparison.Ordinal));
            return new SiemReceiverTestHost(
                application,
                root,
                new Uri(new Uri(ingestAddress), "/v1/logs"),
                new Uri(operatorAddress),
                databasePath,
                preserveRootOnDispose,
                witnessRoot,
                preserveWitnessOnDispose,
                existingAnchorRoot,
                preserveAnchorOnDispose);
        }
        catch
        {
            await DisposeApplicationAndDeleteOwnedRootsAsync(
                application,
                stopApplication: false,
                root,
                preserveRoot: existingRoot is not null,
                witnessRoot,
                preserveWitnessRoot: existingWitnessRoot is not null,
                anchorRoot: null,
                preserveAnchorRoot: true,
                failureHooks?.ClearSqlitePools ?? SqliteConnection.ClearAllPools,
                failureHooks?.DeleteDirectory ?? Directory.Delete);
            throw;
        }
    }

    internal HttpClient CreateClient(X509Certificate2? clientCertificate = null)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = static (_, _, _, errors) =>
                (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None,
        };
        if (clientCertificate is not null) handler.ClientCertificates.Add(clientCertificate);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeApplicationAndDeleteOwnedRootsAsync(
            _application,
            stopApplication: true,
            _root,
            _preserveRootOnDispose,
            _witnessRoot,
            _preserveWitnessOnDispose,
            _anchorRoot,
            _preserveAnchorOnDispose,
            SqliteConnection.ClearAllPools,
            Directory.Delete);
    }

    private static async ValueTask DisposeApplicationAndDeleteOwnedRootsAsync(
        WebApplication? application,
        bool stopApplication,
        string root,
        bool preserveRoot,
        string witnessRoot,
        bool preserveWitnessRoot,
        string? anchorRoot,
        bool preserveAnchorRoot,
        Action clearSqlitePools,
        Action<string, bool> deleteDirectory)
    {
        if (application is not null)
        {
            if (stopApplication) await application.StopAsync();
            await application.DisposeAsync();
        }
        // Microsoft.Data.Sqlite pooling retains idle native handles after the
        // host has disposed every scoped store. POSIX permits unlinking those
        // files, but Windows correctly refuses the recursive cleanup while the
        // pool still owns siem.db. Clearing idle pools is test-process cleanup;
        // checked-out connections remain valid and close when returned.
        clearSqlitePools();
        if (!preserveRoot) deleteDirectory(root, true);
        if (!preserveWitnessRoot) deleteDirectory(witnessRoot, true);
        if (anchorRoot is not null && !preserveAnchorRoot)
            deleteDirectory(anchorRoot, true);
    }
}

internal sealed record SiemReceiverTestHostFailureHooks(
    Func<WebApplication, Task> StartApplication,
    Action ClearSqlitePools,
    Action<string, bool> DeleteDirectory);

internal sealed class RecordingIngestCommitter : IIngestCommitter
{
    private readonly IngestCommitResult _result;
    private readonly Exception? _exception;
    private readonly List<ValidatedOtlpRecord> _records = [];
    private readonly List<IngestReceiptContext> _receipts = [];
    private readonly List<RejectedOtlpAttempt> _quarantines = [];

    internal RecordingIngestCommitter(
        IngestCommitResult? result = null,
        Exception? exception = null)
    {
        _result = result ?? IngestCommitResult.Accepted();
        _exception = exception;
    }

    internal IReadOnlyList<ValidatedOtlpRecord> Records => _records;

    internal IReadOnlyList<IngestReceiptContext> Receipts => _receipts;

    internal IReadOnlyList<RejectedOtlpAttempt> Quarantines => _quarantines;

    public Task<IngestCommitResult> CommitAsync(
        ValidatedOtlpRecord record,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken)
    {
        if (_exception is not null) throw _exception;
        _records.Add(record);
        _receipts.Add(receipt);
        return Task.FromResult(_result);
    }

    public Task<IngestCommitResult> QuarantineAsync(
        RejectedOtlpAttempt attempt,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken)
    {
        if (_exception is not null) throw _exception;
        _quarantines.Add(attempt);
        _receipts.Add(receipt);
        return Task.FromResult(_result.Kind == IngestCommitResultKind.Accepted
            ? IngestCommitResult.Permanent(attempt.FailureCode)
            : _result);
    }
}

internal static class OtlpTestRequest
{
    internal const string DefaultEventId = "018f6a78-4c20-7a11-8a34-1234567890ab";
    internal const string DefaultHostId = "1dd95ad8-53f8-49af-81fe-1d2f720ee790";
    internal const string DefaultSupervisorBootId = "2a6465d4-6652-4ff7-8630-2ab0c5f6d04c";
    private const string OccurredUtc = "2026-07-15T12:34:56.1234567Z";
    private const string ObservedUtc = "2026-07-15T12:34:57.1234567Z";

    /// <summary>One canonical audit-record line plus the identifiers a test
    /// needs to project or chain it; shared by the protobuf request builder
    /// and the R3c JSON ingest tests so both paths validate one shape.</summary>
    internal sealed record TestAuditRecord(
        string Body,
        string EventId,
        string EventType,
        string EventHash,
        string SupervisorBootId,
        long Sequence,
        string SchemaVersion);

    internal static TestAuditRecord CreateRecord(
        string schemaVersion = "ptk.audit/2",
        string? eventId = null,
        string? supervisorBootId = null,
        long sequence = 1,
        string? previousEventHash = null,
        string eventType = "tool.completed",
        string? previousSupervisorBootId = null)
    {
        eventId ??= DefaultEventId;
        supervisorBootId ??= DefaultSupervisorBootId;
        var requestFields = schemaVersion == "ptk.audit/1"
            ? string.Empty
            : "\"evidence_subject_id\":null,\"evidence_subject_digest\":null," +
              "\"evidence_subject_bytes\":null,\"evidence_subject_state\":null," +
              "\"retention_reason\":null";
        var host = schemaVersion == "ptk.audit/3"
            ? ",\"host\":{\"boot_id\":null,\"generation\":null," +
              "\"state\":\"absent\",\"recovery_attempt\":0}"
            : string.Empty;
        var disposition = schemaVersion == "ptk.audit/1"
            ? string.Empty
            : ",\"operator_disposition\":null";
        var callContext = schemaVersion == "ptk.audit/4"
            ? "\"call_attribution\":{\"agent_name\":null," +
              "\"agent_unavailable_reason\":\"not_supplied_by_client\"," +
              "\"model_provider\":null,\"model_name\":null," +
              "\"model_unavailable_reason\":\"not_supplied_by_client\"," +
              "\"source\":null,\"strength\":\"transport_only\"}," +
              "\"client_context\":{\"task_id\":null,\"task_name\":null," +
              "\"mcp_task_ttl_ms\":null," +
              "\"task_unavailable_reason\":\"not_supplied_by_client\"," +
              "\"run_id\":null," +
              "\"run_unavailable_reason\":\"not_supplied_by_client\"," +
              "\"source\":null,\"strength\":\"transport_only\"}," +
              "\"execution_context\":{\"requested_cwd\":null," +
              "\"requested_cwd_unavailable_reason\":\"not_supplied_by_client\"," +
              "\"effective_cwd\":null," +
              "\"effective_cwd_unavailable_reason\":\"not_dispatched\"," +
              "\"repository_root\":null,\"repository_relative_path\":null," +
              "\"repository_unavailable_reason\":\"not_dispatched\"},"
            : string.Empty;
        var preHash =
            "{" +
            $"\"schema_version\":\"{schemaVersion}\"," +
            $"\"event_id\":\"{eventId}\"," +
            $"\"event_type\":\"{eventType}\"," +
            $"\"occurred_utc\":\"{OccurredUtc}\"," +
            $"\"observed_utc\":\"{ObservedUtc}\"," +
            $"\"producer\":{{\"host_id\":\"{DefaultHostId}\",\"supervisor_boot_id\":\"{supervisorBootId}\"," +
            (previousSupervisorBootId is null
                ? string.Empty
                : $"\"previous_supervisor_boot_id\":\"{previousSupervisorBootId}\",") +
            "\"worker_boot_id\":null,\"version\":\"1.2.3\"}" +
            host +
            $",\"sequence\":{sequence},\"previous_event_hash\":" +
            (previousEventHash is null ? "null" : $"\"{previousEventHash}\"") +
            "," +
            "\"session\":{\"name\":null,\"generation\":null}," +
            "\"actor\":{}," +
            callContext +
            "\"correlation\":{\"call_id\":null,\"job_id\":null,\"trace_id\":null}," +
            $"\"request\":{{{requestFields}}}" +
            disposition +
            ",\"routing\":{}," +
            "\"outcome\":{\"state\":\"completed\",\"termination_certainty\":\"confirmed\"}," +
            "\"coverage\":{},\"audit\":{}" +
            "}";
        var eventHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preHash)))
            .ToLowerInvariant();
        var body = preHash[..^1] + $",\"event_hash\":\"{eventHash}\"}}";
        return new TestAuditRecord(
            body,
            eventId,
            eventType,
            eventHash,
            supervisorBootId,
            sequence,
            schemaVersion);
    }

    internal static ExportLogsServiceRequest Create(
        string schemaVersion = "ptk.audit/2",
        string? eventId = null,
        string? supervisorBootId = null,
        long sequence = 1,
        string? previousEventHash = null,
        string eventType = "tool.completed",
        string? previousSupervisorBootId = null)
    {
        var testRecord = CreateRecord(
            schemaVersion,
            eventId,
            supervisorBootId,
            sequence,
            previousEventHash,
            eventType,
            previousSupervisorBootId);
        eventId = testRecord.EventId;
        supervisorBootId = testRecord.SupervisorBootId;
        var eventHash = testRecord.EventHash;
        var body = testRecord.Body;

        var resource = new OtlpResource();
        resource.Attributes.Add(StringAttribute("service.namespace", "ptk"));
        resource.Attributes.Add(StringAttribute("service.name", "powershell-token-killer"));
        resource.Attributes.Add(StringAttribute("service.version", "1.2.3"));
        resource.Attributes.Add(StringAttribute("service.instance.id", supervisorBootId));
        resource.Attributes.Add(StringAttribute("host.id", DefaultHostId));

        var record = new LogRecord
        {
            TimeUnixNano = ToUnixNanoseconds(OccurredUtc),
            ObservedTimeUnixNano = ToUnixNanoseconds(ObservedUtc),
            EventName = $"ptk.audit.{eventType}",
            Body = new AnyValue { StringValue = body },
        };
        record.Attributes.Add(StringAttribute("ptk.audit.schema_version", schemaVersion));
        record.Attributes.Add(StringAttribute("ptk.audit.event_id", eventId));
        record.Attributes.Add(StringAttribute("ptk.audit.event_type", eventType));
        record.Attributes.Add(IntAttribute("ptk.audit.sequence", sequence));
        record.Attributes.Add(StringAttribute("ptk.audit.event_hash", eventHash));
        record.Attributes.Add(StringAttribute("ptk.supervisor.boot_id", supervisorBootId));
        if (previousEventHash is not null)
            record.Attributes.Add(StringAttribute("ptk.audit.previous_event_hash", previousEventHash));
        record.Attributes.Add(StringAttribute("ptk.outcome.state", "completed"));
        record.Attributes.Add(StringAttribute("ptk.termination.certainty", "confirmed"));
        if (schemaVersion == "ptk.audit/3")
        {
            record.Attributes.Add(StringAttribute("ptk.host.state", "absent"));
            record.Attributes.Add(IntAttribute("ptk.host.recovery_attempt", 0));
        }

        var scopeLogs = new ScopeLogs
        {
            Scope = new InstrumentationScope
            {
                Name = "PtkMcpServer.Audit",
                Version = "1.2.3",
            },
        };
        scopeLogs.LogRecords.Add(record);
        var resourceLogs = new ResourceLogs { Resource = resource };
        resourceLogs.ScopeLogs.Add(scopeLogs);
        var request = new ExportLogsServiceRequest();
        request.ResourceLogs.Add(resourceLogs);
        return request;
    }

    internal static ByteArrayContent Content(
        ExportLogsServiceRequest? request = null,
        string contentType = "application/x-protobuf")
    {
        var content = new ByteArrayContent((request ?? Create()).ToByteArray());
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return content;
    }

    private static KeyValue StringAttribute(string key, string value) => new()
    {
        Key = key,
        Value = new AnyValue { StringValue = value },
    };

    private static KeyValue IntAttribute(string key, long value) => new()
    {
        Key = key,
        Value = new AnyValue { IntValue = value },
    };

    private static ulong ToUnixNanoseconds(string timestamp)
    {
        var value = DateTimeOffset.ParseExact(
            timestamp,
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return checked((ulong)(value.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) * 100UL);
    }
}
