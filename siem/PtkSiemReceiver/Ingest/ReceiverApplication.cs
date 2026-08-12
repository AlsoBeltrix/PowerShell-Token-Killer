using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Google.Protobuf;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Net.Http.Headers;
using PtkMcpServer.Audit.OtlpWire;
using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Security;

namespace PtkSiemReceiver.Ingest;

internal static class ReceiverApplication
{
    private const string ProtobufMediaType = "application/x-protobuf";
    internal const int MaximumTlsMaterialBytes = 4 * 1024 * 1024;

    internal static WebApplication Build(
        SiemReceiverOptions options,
        string[]? args = null,
        IIngestCommitter? committer = null,
        TimeProvider? timeProvider = null,
        Storage.ISqliteIngestFaultInjector? storageFaultInjector = null,
        ProtectedPathTestHooks? protectedPathTestHooks = null,
        Action? tlsMaterialAcquiredForTests = null,
        bool alertEvaluationHoldForTests = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        RejectMutableStorageCollisions(options);

        var sensitiveBuffers = new List<byte[]>();
        try
        {
            var protectedExternalIdentities = new HashSet<ProtectedPathIdentity>();
            if (options.ConfigurationIdentity is { } configurationIdentity)
                protectedExternalIdentities.Add(configurationIdentity);

            var serverCertificateRead = ReadTlsMaterial(
                options.ServerCertificatePath,
                "tls_protection",
                "server_certificate",
                sensitiveBuffers,
                protectedPathTestHooks);
            protectedExternalIdentities.Add(serverCertificateRead.Identity);
            var serverKeyRead = ReadTlsMaterial(
                options.ServerCertificateKeyPath,
                "tls_protection",
                "server_certificate",
                sensitiveBuffers,
                protectedPathTestHooks);
            protectedExternalIdentities.Add(serverKeyRead.Identity);
            var authorityReads = options.ClientCaBundlePaths
                .Select(path => ReadTlsMaterial(
                    path,
                    "tls_protection",
                    "client_ca_bundle",
                    sensitiveBuffers,
                    protectedPathTestHooks))
                .ToArray();
            foreach (var authorityRead in authorityReads)
                protectedExternalIdentities.Add(authorityRead.Identity);
            ProtectedFileRead? operatorCertificateRead = null;
            ProtectedFileRead? operatorKeyRead = null;
            if (options.OperatorHttpsCertificatePath is not null)
            {
                operatorCertificateRead = ReadTlsMaterial(
                    options.OperatorHttpsCertificatePath,
                    "operator_https_material",
                    "operator_https_material",
                    sensitiveBuffers,
                    protectedPathTestHooks);
                protectedExternalIdentities.Add(operatorCertificateRead.Value.Identity);
                operatorKeyRead = ReadTlsMaterial(
                    options.OperatorHttpsCertificateKeyPath!,
                    "operator_https_material",
                    "operator_https_material",
                    sensitiveBuffers,
                    protectedPathTestHooks);
                protectedExternalIdentities.Add(operatorKeyRead.Value.Identity);
            }

            tlsMaterialAcquiredForTests?.Invoke();
            var serverCertificate = ReceiverCertificateLoader.LoadServerCertificate(
                serverCertificateRead.Bytes,
                serverKeyRead.Bytes);
            IReadOnlyList<X509Certificate2> clientAuthorities;
            try
            {
                clientAuthorities = ReceiverCertificateLoader.LoadAuthorities(
                    authorityReads.Select(read => read.Bytes).ToArray());
            }
            catch
            {
                serverCertificate.Dispose();
                throw;
            }
            var trustStore = new ReceiverTrustStore(clientAuthorities);
            // The operator surface serves HTTPS from its own verified bytes
            // when configured (S5); without a pair it is plain HTTP, which
            // the configuration loader already restricts to loopback.
            X509Certificate2? operatorCertificate = null;
            if (operatorCertificateRead is not null)
            {
                try
                {
                    operatorCertificate = ReceiverCertificateLoader.LoadServerCertificate(
                        operatorCertificateRead.Value.Bytes,
                        operatorKeyRead!.Value.Bytes);
                }
                catch
                {
                    serverCertificate.Dispose();
                    trustStore.Dispose();
                    throw;
                }
            }
            Storage.SqliteIngestStore? ownedStore = null;
            Storage.CustodyWitness? ownedWitness = null;
            WebApplication? application = null;
            try
            {
                if (committer is null)
                {
                    // Open and complete storage protection before Kestrel can bind.
                    ownedStore = Storage.SqliteIngestStore.Open(
                        options.SqlitePath,
                        storageFaultInjector,
                        protectedPathTestHooks,
                        protectedExternalIdentities,
                        options.AlertRuleConfigHash);
                    if (options.CustodyWitness is not null)
                    {
                        ownedWitness = Storage.CustodyWitness.Open(
                            options.CustodyWitness,
                            ownedStore,
                            timeProvider ?? TimeProvider.System,
                            protectedPathTestHooks);
                    }
                }

                var builder = WebApplication.CreateSlimBuilder(args ?? []);
                builder.WebHost.ConfigureKestrel(kestrel =>
                {
                    kestrel.AddServerHeader = false;
                    // rbc-10: never leave the transport unbounded. The cap sits
                    // exactly one byte above the application bound so that
                    // ReadBoundedAsync still observes the first overflowing byte
                    // and returns the deterministic OTLP request_too_large
                    // response instead of a Kestrel 413 connection abort.
                    kestrel.Limits.MaxRequestBodySize = (long)options.MaxRequestBytes + 1;
                    kestrel.Listen(options.IngestBindAddress, options.IngestPort, listen =>
                    {
                        listen.Protocols = HttpProtocols.Http1AndHttp2;
                        listen.UseHttps(new HttpsConnectionAdapterOptions
                        {
                            ServerCertificate = serverCertificate,
                            // With an ingest token configured (R3c), a client
                            // may authenticate per-request with Bearer
                            // instead; a certificate that IS presented is
                            // still validated exactly as before. Without a
                            // token, mTLS stays mandatory.
                            ClientCertificateMode = options.IngestToken is null
                                ? ClientCertificateMode.RequireCertificate
                                : ClientCertificateMode.AllowCertificate,
                            ClientCertificateValidation = (certificate, chain, errors) =>
                                ClientCertificateValidator.Validate(
                                    certificate,
                                    chain,
                                    errors,
                                    trustStore.Authorities,
                                    options.RevocationCheckMode),
                            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        });
                    });
                    // The operator query surface is a separate listener by
                    // design (S5); its connections carry a marker feature so
                    // neither surface can serve the other's routes, whatever
                    // ports the deployment chose.
                    kestrel.Listen(options.OperatorBindAddress, options.OperatorPort, listen =>
                    {
                        listen.Use(next => connection =>
                        {
                            connection.Features.Set<Web.IOperatorSurfaceFeature>(
                                Web.OperatorSurfaceFeature.Instance);
                            return next(connection);
                        });
                        if (operatorCertificate is not null)
                        {
                            listen.Protocols = HttpProtocols.Http1AndHttp2;
                            listen.UseHttps(new HttpsConnectionAdapterOptions
                            {
                                ServerCertificate = operatorCertificate,
                                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            });
                        }
                    });
                });

                builder.Services.AddSingleton(options);
                builder.Services.AddSingleton<X509Certificate2>(_ => serverCertificate);
                builder.Services.AddSingleton<ReceiverTrustStore>(_ => trustStore);
                builder.Services.AddSingleton(_ => new Web.OperatorTlsMaterial(operatorCertificate));
                builder.Services.AddSingleton(timeProvider ?? TimeProvider.System);
                var custodyHealth = ownedWitness?.HealthState ?? new Storage.CustodyHealthState();
                builder.Services.AddSingleton(custodyHealth);
                builder.Services.AddSingleton<IngestAdmissionGate>(
                    _ => new IngestAdmissionGate(options.MaxConcurrentRequests));
                if (ownedStore is not null)
                {
                    builder.Services.AddSingleton<IIngestCommitter>(_ => ownedStore);
                    // The operator surface's gap-disposition write (S6) goes
                    // through the same serialized writer as ingest.
                    builder.Services.AddSingleton<Storage.SqliteIngestStore>(_ => ownedStore);
                    if (ownedWitness is not null)
                    {
                        builder.Services.AddSingleton<Storage.CustodyWitness>(_ => ownedWitness);
                        builder.Services.AddHostedService<Storage.CustodyWitnessService>();
                    }
                }
                else
                {
                    builder.Services.AddSingleton(committer!);
                }
                builder.Services.AddHostedService<ReceiverLifecycleService>();
                // Alert evaluation is decoupled from the ack path but
                // crash-safe: the queue rows were committed by ingest, and
                // the hold flag exists so a test can crash "before alert
                // persistence" deterministically.
                if (ownedStore is not null && !alertEvaluationHoldForTests)
                {
                    builder.Services.AddHostedService(serviceProvider =>
                        new Alerting.AlertEvaluationService(
                            serviceProvider.GetRequiredService<SiemReceiverOptions>(),
                            serviceProvider.GetRequiredService<Storage.SqliteIngestStore>(),
                            serviceProvider.GetRequiredService<
                                ILogger<Alerting.AlertEvaluationService>>(),
                            serviceProvider.GetRequiredService<TimeProvider>(),
                            serviceProvider.GetRequiredService<Storage.CustodyHealthState>()));
                }
                // Retention is enforced, not merely configured (rbc-11).
                builder.Services.AddHostedService(serviceProvider =>
                    new PtkSiemReceiver.Storage.RetentionService(
                        serviceProvider.GetRequiredService<SiemReceiverOptions>(),
                        serviceProvider.GetRequiredService<IIngestCommitter>(),
                        serviceProvider.GetRequiredService<
                            ILogger<PtkSiemReceiver.Storage.RetentionService>>(),
                        timeProvider: serviceProvider.GetRequiredService<TimeProvider>(),
                        custodyHealth: serviceProvider.GetRequiredService<Storage.CustodyHealthState>()));

                application = builder.Build();
                // Ensure the container owns all captured disposable singletons.
                _ = application.Services.GetRequiredService<X509Certificate2>();
                _ = application.Services.GetRequiredService<ReceiverTrustStore>();
                _ = application.Services.GetRequiredService<IIngestCommitter>();
                _ = application.Services.GetRequiredService<IngestAdmissionGate>();
                _ = application.Services.GetRequiredService<Web.OperatorTlsMaterial>();
                application.Use(async (context, next) =>
                {
                    if (context.Request.Path == "/v1/logs" &&
                        !context.RequestServices
                            .GetRequiredService<Storage.CustodyHealthState>()
                            .CanMutate)
                    {
                        await OtlpHttpResponse.WriteTransientAsync(
                            context.Response,
                            "custody_unhealthy",
                            context.RequestAborted).ConfigureAwait(false);
                        return;
                    }
                    await next(context).ConfigureAwait(false);
                });
                application.MapPost("/v1/logs", HandleIngestAsync);
                Web.OperatorEndpoints.Map(application);
                return application;
            }
            catch
            {
                if (application is not null)
                {
                    application.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                else
                {
                    ownedWitness?.Dispose();
                    ownedStore?.Dispose();
                    serverCertificate.Dispose();
                    trustStore.Dispose();
                    operatorCertificate?.Dispose();
                }
                throw;
            }
        }
        finally
        {
            foreach (var buffer in sensitiveBuffers)
                CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void RejectMutableStorageCollisions(SiemReceiverOptions options)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var storagePaths = new[]
        {
            options.SqlitePath,
            options.SqlitePath + "-wal",
            options.SqlitePath + "-shm",
        };
        var externalPaths = new List<string>
        {
            options.ServerCertificatePath,
            options.ServerCertificateKeyPath,
        };
        externalPaths.AddRange(options.ClientCaBundlePaths);
        if (options.ConfigurationPath is not null)
            externalPaths.Add(options.ConfigurationPath);
        if (options.OperatorHttpsCertificatePath is not null)
        {
            externalPaths.Add(options.OperatorHttpsCertificatePath);
            externalPaths.Add(options.OperatorHttpsCertificateKeyPath!);
        }

        if (options.CustodyWitness is not null)
        {
            var dataRoot = Path.GetDirectoryName(options.SqlitePath)!;
            if (IsSameOrDescendant(options.CustodyWitness.DirectoryPath, dataRoot) ||
                (options.CustodyWitness.AnchorDirectoryPath is { } anchor &&
                 (IsSameOrDescendant(anchor, dataRoot) ||
                  IsSameOrDescendant(anchor, options.CustodyWitness.DirectoryPath) ||
                  IsSameOrDescendant(options.CustodyWitness.DirectoryPath, anchor))))
            {
                throw new SiemReceiverStartupException("custody_witness_independence");
            }
        }

        if (externalPaths.Any(externalPath =>
                storagePaths.Any(storagePath =>
                    string.Equals(externalPath, storagePath, comparison))))
        {
            throw new SiemReceiverStartupException("protected_path_collision");
        }
    }

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." ||
               (!Path.IsPathFullyQualified(relative) &&
                relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static ProtectedFileRead ReadTlsMaterial(
        string path,
        string protectionFailureCode,
        string emptyFailureCode,
        ICollection<byte[]> ownedBuffers,
        ProtectedPathTestHooks? testHooks)
    {
        ProtectedFileRead protectedRead;
        try
        {
            protectedRead = SiemProtectedPath.ReadExternalFileWithIdentity(
                path,
                MaximumTlsMaterialBytes,
                testHooks);
        }
        catch (ProtectedPathException exception)
        {
            throw new SiemReceiverStartupException(protectionFailureCode, exception);
        }

        ownedBuffers.Add(protectedRead.Bytes);
        if (protectedRead.Bytes.Length == 0)
            throw new SiemReceiverStartupException(emptyFailureCode);
        return protectedRead;
    }

    // internal (not private) solely so PtkSiemReceiver.Tests can pin the
    // rbc-12 refusal-before-buffering contract with a throwing body stream.
    internal static async Task HandleIngestAsync(
        HttpContext context,
        SiemReceiverOptions options,
        IIngestCommitter committer,
        TimeProvider timeProvider,
        IngestAdmissionGate admissionGate)
    {
        // Ingest never serves on the operator surface (S5): the operator
        // credential must not become an ingest credential by port reuse.
        if (context.Features.Get<Web.IOperatorSurfaceFeature>() is not null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        // rbc-12: refuse before any buffering so a saturated receiver
        // holds no additional per-request memory.
        if (!admissionGate.TryEnter())
        {
            await OtlpHttpResponse.WriteTransientAsync(
                context.Response,
                "admission_capacity",
                context.RequestAborted);
            return;
        }

        try
        {
            await HandleAdmittedIngestAsync(context, options, committer, timeProvider);
        }
        finally
        {
            admissionGate.Exit();
        }
    }

    private static async Task HandleAdmittedIngestAsync(
        HttpContext context,
        SiemReceiverOptions options,
        IIngestCommitter committer,
        TimeProvider timeProvider)
    {
        var receivedUtc = timeProvider.GetUtcNow();
        // Authentication precedes any body buffering (the rbc-12 posture): a
        // connection either carried a validated client certificate through
        // the TLS handshake, or — token mode only — must present the exact
        // ingest bearer token now.
        var certificate = context.Connection.ClientCertificate;
        if (certificate is null && !HasValidIngestToken(context.Request, options.IngestToken))
        {
            await OtlpHttpResponse.WriteUnauthorizedAsync(
                context.Response,
                context.RequestAborted);
            return;
        }

        var encoding = ClassifyContentType(context.Request);
        if (encoding == IngestEncoding.Unsupported)
        {
            await OtlpHttpResponse.WritePermanentAsync(
                context.Response,
                "content_type",
                context.RequestAborted);
            return;
        }

        var body = await ReadBoundedAsync(
            context.Request.Body,
            options.MaxRequestBytes,
            context.RequestAborted);
        if (body is null)
        {
            await OtlpHttpResponse.WritePermanentAsync(
                context.Response,
                "request_too_large",
                context.RequestAborted);
            return;
        }

        var receipt = CreateReceiptContext(context, receivedUtc, options);
        if (receipt is null)
        {
            await OtlpHttpResponse.WriteTransientAsync(
                context.Response,
                "connection_metadata",
                context.RequestAborted);
            return;
        }

        if (encoding == IngestEncoding.Protobuf)
        {
            var validation = OtlpRequestValidator.Validate(body);
            var commitResult = validation.IsValid
                ? await InvokeCommitAsync(
                    () => committer.CommitAsync(
                        validation.Record!,
                        receipt,
                        context.RequestAborted),
                    context.RequestAborted)
                : await InvokeCommitAsync(
                    () => committer.QuarantineAsync(
                        validation.RejectedAttempt!,
                        receipt,
                        context.RequestAborted),
                    context.RequestAborted);

            await WriteCommitResultAsync(context, commitResult);
            return;
        }

        await HandleJsonIngestAsync(context, committer, receipt, body);
    }

    /// <summary>
    /// The OTLP/HTTP JSON path (audit-restoration R3c): the encoding PTK's
    /// own exporter sends, batched. Each record commits or quarantines
    /// individually; the response aggregates so the producer's existing
    /// contract does the rest — transient stops the pass and replays the
    /// batch (commits are idempotent by exact bytes), permanent makes the
    /// producer isolate record-by-record so one poison record costs one
    /// record.
    /// </summary>
    private static async Task HandleJsonIngestAsync(
        HttpContext context,
        IIngestCommitter committer,
        IngestReceiptContext receipt,
        byte[] body)
    {
        var validation = OtlpRequestValidator.ValidateJsonRequest(body);
        if (validation.RequestFailureCode is not null)
        {
            await OtlpHttpResponse.WritePermanentAsync(
                context.Response,
                validation.RequestFailureCode,
                context.RequestAborted);
            return;
        }

        IngestCommitResult? firstPermanent = null;
        foreach (var result in validation.Results)
        {
            var commitResult = result.IsValid
                ? await InvokeCommitAsync(
                    () => committer.CommitAsync(
                        result.Record!,
                        receipt,
                        context.RequestAborted),
                    context.RequestAborted)
                : await InvokeCommitAsync(
                    () => committer.QuarantineAsync(
                        result.RejectedAttempt!,
                        receipt,
                        context.RequestAborted),
                    context.RequestAborted);
            if (commitResult.Kind == IngestCommitResultKind.TransientFailure)
            {
                // Stop at the first transient refusal: the whole request is
                // retried and already-committed records replay idempotently.
                await WriteCommitResultAsync(context, commitResult);
                return;
            }
            if (commitResult.Kind == IngestCommitResultKind.PermanentFailure)
                firstPermanent ??= commitResult;
        }

        await WriteCommitResultAsync(
            context,
            firstPermanent ?? IngestCommitResult.Accepted());
    }

    private enum IngestEncoding
    {
        Unsupported,
        Protobuf,
        Json,
    }

    private static async Task<IngestCommitResult> InvokeCommitAsync(
        Func<Task<IngestCommitResult>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return IngestCommitResult.Transient("commit_failed");
        }
    }

    private static async Task WriteCommitResultAsync(
        HttpContext context,
        IngestCommitResult commitResult)
    {
        if (commitResult.Kind == IngestCommitResultKind.Accepted)
        {
            await OtlpHttpResponse.WriteSuccessAsync(context.Response, context.RequestAborted);
        }
        else if (commitResult.Kind == IngestCommitResultKind.PermanentFailure)
        {
            await OtlpHttpResponse.WritePermanentAsync(
                context.Response,
                commitResult.FailureCode,
                context.RequestAborted);
        }
        else
        {
            await OtlpHttpResponse.WriteTransientAsync(
                context.Response,
                commitResult.FailureCode,
                context.RequestAborted);
        }
    }

    private static IngestReceiptContext? CreateReceiptContext(
        HttpContext context,
        DateTimeOffset receivedUtc,
        SiemReceiverOptions options)
    {
        var certificate = context.Connection.ClientCertificate;
        var address = context.Connection.RemoteIpAddress;
        if (address is null) return null;
        // The custody credential identity: the certificate's SHA-256 for an
        // mTLS client, the token's SHA-256 for a bearer client (R3c) —
        // either way 64 lower-hex characters naming the credential that
        // delivered the record, never the credential itself.
        string thumbprint;
        if (certificate is not null)
        {
            thumbprint = Convert.ToHexString(SHA256.HashData(certificate.RawData))
                .ToLowerInvariant();
        }
        else if (options.IngestToken is { } token)
        {
            thumbprint = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(token)))
                .ToLowerInvariant();
        }
        else
        {
            return null;
        }

        var addressText = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        var endpoint = $"{addressText}:{context.Connection.RemotePort}";
        return new IngestReceiptContext(
            receivedUtc.ToUniversalTime(),
            thumbprint,
            endpoint);
    }

    /// <summary>Exact single Authorization header, exact Bearer scheme,
    /// fixed-time token comparison. Only meaningful when a token is
    /// configured; without one, certificate-less connections cannot exist.</summary>
    private static bool HasValidIngestToken(HttpRequest request, string? configuredToken)
    {
        if (configuredToken is null) return false;
        if (!request.Headers.TryGetValue(HeaderNames.Authorization, out var values) ||
            values.Count != 1 ||
            values[0] is not { } header)
        {
            return false;
        }

        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.Ordinal)) return false;
        var presented = Encoding.UTF8.GetBytes(header[scheme.Length..]);
        var expected = Encoding.UTF8.GetBytes(configuredToken);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    private const string JsonMediaType = "application/json";

    private static IngestEncoding ClassifyContentType(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderNames.ContentType, out var values) ||
            values.Count != 1 ||
            !MediaTypeHeaderValue.TryParse(values[0], out var parsed))
        {
            return IngestEncoding.Unsupported;
        }

        if (string.Equals(
                parsed.MediaType.Value,
                ProtobufMediaType,
                StringComparison.OrdinalIgnoreCase) &&
            parsed.Parameters.Count == 0)
        {
            return IngestEncoding.Protobuf;
        }

        // application/json, alone or with the one charset UTF-8 JSON has
        // (PTK's exporter sends "application/json; charset=utf-8").
        if (string.Equals(
                parsed.MediaType.Value,
                JsonMediaType,
                StringComparison.OrdinalIgnoreCase))
        {
            if (parsed.Parameters.Count == 0) return IngestEncoding.Json;
            if (parsed.Parameters.Count == 1 &&
                string.Equals(parsed.Parameters[0].Name.Value, "charset", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parsed.Parameters[0].Value.Value, "utf-8", StringComparison.OrdinalIgnoreCase))
            {
                return IngestEncoding.Json;
            }
        }

        return IngestEncoding.Unsupported;
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(
                    rented.AsMemory(0, rented.Length),
                    cancellationToken);
                if (read == 0) return buffer.ToArray();
                if (buffer.Length + read > maximumBytes) return null;
                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}

internal interface IIngestCommitter
{
    // Compatibility seam for the isolated producer conformance fixture from S2.
    Task<IngestCommitResult> CommitAsync(
        ValidatedOtlpRecord record,
        CancellationToken cancellationToken) =>
        Task.FromException<IngestCommitResult>(
            new NotSupportedException("Receipt metadata is required by the production store."));

    Task<IngestCommitResult> CommitAsync(
        ValidatedOtlpRecord record,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken) =>
        CommitAsync(record, cancellationToken);

    Task<IngestCommitResult> QuarantineAsync(
        RejectedOtlpAttempt attempt,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken) =>
        Task.FromResult(IngestCommitResult.Permanent(attempt.FailureCode));
}

internal sealed record IngestReceiptContext(
    DateTimeOffset ReceivedUtc,
    string ClientCertificateThumbprint,
    string RemoteEndpoint);

internal enum IngestCommitResultKind
{
    Accepted,
    PermanentFailure,
    TransientFailure,
}

internal readonly record struct IngestCommitResult(
    IngestCommitResultKind Kind,
    string FailureCode)
{
    internal static IngestCommitResult Accepted() =>
        new(IngestCommitResultKind.Accepted, string.Empty);

    internal static IngestCommitResult Permanent(string failureCode) =>
        new(IngestCommitResultKind.PermanentFailure, failureCode);

    internal static IngestCommitResult Transient(string failureCode) =>
        new(IngestCommitResultKind.TransientFailure, failureCode);
}
