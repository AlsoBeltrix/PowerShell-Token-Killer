using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PtkMcpServer.Audit.OtlpWire;
using PtkSiemReceiver.Security;

namespace PtkSiemReceiver.Tests;

[Collection(SiemReceiverProcessCollection.Name)]
public sealed class ReceiverProcessBarrierTests
{
    [Fact]
    public async Task Whole_tree_kill_before_commit_returns_no_valid_ack_or_durable_state()
    {
        await using var fixture = await ReceiverProcessFixture.CreateAsync();
        await using var receiver = await fixture.StartAsync(ReceiverProcessMode.CommitBarrier);
        using var client = fixture.CreateIngestClient();
        var responseTask = client.PostAsync(
            fixture.IngestEndpoint,
            OtlpTestRequest.Content());

        await fixture.WaitForCommitBarrierAsync();
        await receiver.KillTreeAsync();

        Assert.False(await IsValidNonRejectingAckAsync(responseTask));
        await using var restarted = await fixture.StartAsync(ReceiverProcessMode.Production);
        Assert.Equal(0L, DatabaseInt64(fixture.DatabasePath, "SELECT COUNT(*) FROM events;"));
        Assert.Equal(0L, DatabaseInt64(fixture.DatabasePath, "SELECT COUNT(*) FROM chains;"));
        Assert.Equal(0L, DatabaseInt64(fixture.DatabasePath, "SELECT COUNT(*) FROM custody;"));
    }

    [Fact]
    public async Task Valid_ack_survives_immediate_tree_kill_with_event_chain_custody_and_replay()
    {
        await using var fixture = await ReceiverProcessFixture.CreateAsync();
        var request = OtlpTestRequest.Create();
        var expected = OtlpTestRequest.CreateRecord();
        var requestBytes = request.ToByteArray();
        await using (var receiver = await fixture.StartAsync(ReceiverProcessMode.Production))
        {
            using var client = fixture.CreateIngestClient();
            using var response = await client.PostAsync(
                fixture.IngestEndpoint,
                OtlpTestRequest.Content(request));
            AssertValidNonRejectingAck(response);
            await receiver.KillTreeAsync();
        }

        await using var restarted = await fixture.StartAsync(ReceiverProcessMode.Production);
        using var operatorClient = fixture.CreateOperatorClient();
        using (var detailResponse = await operatorClient.GetAsync(
                   new Uri(fixture.OperatorEndpoint, $"/api/events/{expected.EventId}")))
        {
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            using var payload = JsonDocument.Parse(
                await detailResponse.Content.ReadAsByteArrayAsync());
            var eventDetail = payload.RootElement.GetProperty("event");
            Assert.Equal(expected.Body, eventDetail.GetProperty("body").GetString());
            Assert.Equal(expected.EventHash, eventDetail.GetProperty("event_hash").GetString());
            var chain = payload.RootElement.GetProperty("chain");
            Assert.Equal(1, chain.GetProperty("head_sequence").GetInt64());
            Assert.Equal(expected.EventId, chain.GetProperty("head_event_id").GetString());
            Assert.Equal(expected.EventHash, chain.GetProperty("head_event_hash").GetString());
        }
        using (var healthResponse = await operatorClient.GetAsync(
                   new Uri(fixture.OperatorEndpoint, "/api/custody/health")))
        {
            Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
            using var payload = JsonDocument.Parse(
                await healthResponse.Content.ReadAsByteArrayAsync());
            Assert.True(payload.RootElement.GetProperty("healthy").GetBoolean());
            Assert.False(payload.RootElement.GetProperty("restore_pending").GetBoolean());
            Assert.Equal(1, payload.RootElement.GetProperty("custody_sequence").GetInt64());
            Assert.False(string.IsNullOrWhiteSpace(
                payload.RootElement.GetProperty("custody_hash").GetString()));
        }
        Assert.Equal(requestBytes, DatabaseBytes(
            fixture.DatabasePath, "SELECT raw_request FROM events;"));
        Assert.Equal("accepted", DatabaseString(
            fixture.DatabasePath, "SELECT disposition FROM custody;"));
        Assert.Equal("event", DatabaseString(
            fixture.DatabasePath, "SELECT subject_kind FROM custody;"));
        Assert.Equal(expected.EventId, DatabaseString(
            fixture.DatabasePath, "SELECT subject_id FROM custody;"));

        using (var ingestClient = fixture.CreateIngestClient())
        using (var replay = await ingestClient.PostAsync(
                   fixture.IngestEndpoint,
                   OtlpTestRequest.Content(request)))
        {
            AssertValidNonRejectingAck(replay);
        }
        Assert.Equal(1L, DatabaseInt64(fixture.DatabasePath, "SELECT COUNT(*) FROM events;"));
        Assert.Equal(1L, DatabaseInt64(fixture.DatabasePath, "SELECT COUNT(*) FROM chains;"));
        Assert.Equal(1L, DatabaseInt64(fixture.DatabasePath, "SELECT COUNT(*) FROM custody;"));
    }

    [Fact]
    public async Task Ack_before_commit_double_fails_the_post_ack_process_barrier()
    {
        await using var fixture = await ReceiverProcessFixture.CreateAsync();
        await using (var receiver = await fixture.StartAsync(ReceiverProcessMode.AckBeforeCommit))
        {
            using var client = fixture.CreateIngestClient();
            using var response = await client.PostAsync(
                fixture.IngestEndpoint,
                OtlpTestRequest.Content());
            AssertValidNonRejectingAck(response);
            await receiver.KillTreeAsync();
        }

        await using var restarted = await fixture.StartAsync(ReceiverProcessMode.Production);
        Assert.Equal(0L, DatabaseInt64(fixture.DatabasePath, "SELECT COUNT(*) FROM events;"));
        Assert.Equal(0L, DatabaseInt64(fixture.DatabasePath, "SELECT COUNT(*) FROM custody;"));
    }

    private static async Task<bool> IsValidNonRejectingAckAsync(
        Task<HttpResponseMessage> responseTask)
    {
        try
        {
            using var response = await responseTask;
            return IsValidNonRejectingAck(response);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
    }

    private static void AssertValidNonRejectingAck(HttpResponseMessage response) =>
        Assert.True(IsValidNonRejectingAck(response),
            $"Expected an exact nonrejecting OTLP ack, got {(int)response.StatusCode}.");

    private static bool IsValidNonRejectingAck(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentType?.ToString() != "application/x-protobuf")
        {
            return false;
        }

        var body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        try
        {
            return ExportLogsServiceResponse.Parser.ParseFrom(body).PartialSuccess is null;
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
    }

    private static byte[] DatabaseBytes(string path, string sql) =>
        Assert.IsType<byte[]>(DatabaseScalar(path, sql));

    private static string DatabaseString(string path, string sql) =>
        Assert.IsType<string>(DatabaseScalar(path, sql));

    private static long DatabaseInt64(string path, string sql) =>
        Assert.IsType<long>(DatabaseScalar(path, sql));

    private static object DatabaseScalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() ?? DBNull.Value;
    }
}

internal enum ReceiverProcessMode
{
    Production,
    CommitBarrier,
    AckBeforeCommit,
}

internal sealed class ReceiverProcessFixture : IAsyncDisposable
{
    private const string IngestToken = "ingest-token-for-process-barriers";
    private const string OperatorToken = "operator-token-for-process-barriers";
    private const string BarrierEnvironment = "PTK_SIEM_TEST_COMMIT_BARRIER";
    private const string AckBeforeCommitEnvironment = "PTK_SIEM_TEST_ACK_BEFORE_COMMIT";
    private readonly string _dataRoot;
    private readonly string _witnessRoot;
    private readonly string _controlRoot;
    private readonly string _configurationPath;
    private readonly List<ReceiverProcess> _processes = [];

    private ReceiverProcessFixture(
        string dataRoot,
        string witnessRoot,
        string controlRoot,
        string configurationPath,
        string databasePath,
        Uri ingestEndpoint,
        Uri operatorEndpoint)
    {
        _dataRoot = dataRoot;
        _witnessRoot = witnessRoot;
        _controlRoot = controlRoot;
        _configurationPath = configurationPath;
        DatabasePath = databasePath;
        IngestEndpoint = ingestEndpoint;
        OperatorEndpoint = operatorEndpoint;
    }

    internal string DatabasePath { get; }

    internal Uri IngestEndpoint { get; }

    internal Uri OperatorEndpoint { get; }

    internal static Task<ReceiverProcessFixture> CreateAsync()
    {
        var dataRoot = SiemTestFileSystem.CreateProtectedRoot("ptk-siem-process-data");
        var witnessRoot = SiemTestFileSystem.CreateProtectedRoot("ptk-siem-process-witness");
        var controlRoot = SiemTestFileSystem.CreateProtectedRoot("ptk-siem-process-control");
        try
        {
            using var authority = new TestCertificateAuthority();
            using var root = authority.Root;
            using var server = authority.IssueServer();
            var certificatePath = SiemTestFileSystem.WriteProtectedText(
                dataRoot, "server-cert.pem", server.ExportCertificatePem());
            string keyText;
            using (var key = server.GetRSAPrivateKey() ??
                             throw new InvalidOperationException("The server certificate has no RSA key."))
            {
                keyText = key.ExportPkcs8PrivateKeyPem();
            }
            var keyPath = SiemTestFileSystem.WriteProtectedText(dataRoot, "server-key.pem", keyText);
            var authorityPath = SiemTestFileSystem.WriteProtectedText(
                dataRoot, "client-roots.pem", root.ExportCertificatePem());
            var databasePath = Path.Combine(dataRoot, "siem.db");
            var (ingestPort, operatorPort) = ReservePorts();
            var configurationPath = SiemTestFileSystem.WriteProtectedText(
                dataRoot,
                "receiver.json",
                JsonSerializer.Serialize(new
                {
                    ingest = new
                    {
                        bindAddress = "127.0.0.1",
                        port = ingestPort,
                        serverCertificatePath = certificatePath,
                        serverCertificateKeyPath = keyPath,
                        clientCaBundlePaths = new[] { authorityPath },
                        revocationCheckMode = "NoCheck",
                        token = IngestToken,
                    },
                    @operator = new
                    {
                        bindAddress = "127.0.0.1",
                        port = operatorPort,
                        token = OperatorToken,
                    },
                    storage = new
                    {
                        sqlitePath = databasePath,
                        custodyWitness = new
                        {
                            directoryPath = witnessRoot,
                            checkpointIntervalSeconds = 3_600,
                        },
                    },
                }));
            return Task.FromResult(new ReceiverProcessFixture(
                dataRoot,
                witnessRoot,
                controlRoot,
                configurationPath,
                databasePath,
                new Uri($"https://127.0.0.1:{ingestPort}/v1/logs"),
                new Uri($"http://127.0.0.1:{operatorPort}/")));
        }
        catch
        {
            DeleteBestEffort(controlRoot);
            DeleteBestEffort(witnessRoot);
            DeleteBestEffort(dataRoot);
            throw;
        }
    }

    internal async Task<ReceiverProcess> StartAsync(ReceiverProcessMode mode)
    {
        File.Delete(Path.Combine(_controlRoot, "entered"));
        File.Delete(Path.Combine(_controlRoot, "release"));
        var executable = mode == ReceiverProcessMode.Production
            ? FindProductionReceiverExecutable()
            : FindTestHostExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["PTK_SIEM_CONFIG"] = _configurationPath;
        startInfo.Environment.Remove(BarrierEnvironment);
        startInfo.Environment.Remove(AckBeforeCommitEnvironment);
        if (mode is ReceiverProcessMode.CommitBarrier or ReceiverProcessMode.AckBeforeCommit)
            startInfo.Environment[BarrierEnvironment] = _controlRoot;
        if (mode == ReceiverProcessMode.AckBeforeCommit)
            startInfo.Environment[AckBeforeCommitEnvironment] = "1";

        var process = Process.Start(startInfo) ??
                      throw new InvalidOperationException("Could not start the receiver test host.");
        var receiver = new ReceiverProcess(process);
        _processes.Add(receiver);
        await receiver.WaitUntilReadyAsync(OperatorEndpoint, OperatorToken);
        return receiver;
    }

    internal HttpClient CreateIngestClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IngestToken);
        return client;
    }

    internal HttpClient CreateOperatorClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", OperatorToken);
        return client;
    }

    internal async Task WaitForCommitBarrierAsync()
    {
        var enteredPath = Path.Combine(_controlRoot, "entered");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(enteredPath))
            await Task.Delay(20, timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
            await process.DisposeAsync();
        DeleteBestEffort(_controlRoot);
        DeleteBestEffort(_witnessRoot);
        DeleteBestEffort(_dataRoot);
    }

    private static (int Ingest, int Operator) ReservePorts()
    {
        var ingest = new TcpListener(IPAddress.Loopback, 0);
        var @operator = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            ingest.Start();
            @operator.Start();
            return (
                ((IPEndPoint)ingest.LocalEndpoint).Port,
                ((IPEndPoint)@operator.LocalEndpoint).Port);
        }
        finally
        {
            @operator.Stop();
            ingest.Stop();
        }
    }

    private static string FindTestHostExecutable()
    {
        var targetDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configurationDirectory = targetDirectory.Parent ??
                                     throw new InvalidOperationException("Test configuration directory missing.");
        var testsProject = configurationDirectory.Parent?.Parent ??
                           throw new InvalidOperationException("Test project directory missing.");
        var siemDirectory = testsProject.Parent ??
                            throw new InvalidOperationException("SIEM directory missing.");
        var fileName = OperatingSystem.IsWindows()
            ? "PtkSiemReceiver.TestHost.exe"
            : "PtkSiemReceiver.TestHost";
        var path = Path.Combine(
            siemDirectory.FullName,
            "PtkSiemReceiver.TestHost",
            "bin",
            configurationDirectory.Name,
            targetDirectory.Name,
            fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("The receiver test host was not built.", path);
        return path;
    }

    private static string FindProductionReceiverExecutable()
    {
        var fileName = OperatingSystem.IsWindows()
            ? "PtkSiemReceiver.exe"
            : "PtkSiemReceiver";
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("The standalone receiver was not built.", path);
        return path;
    }

    private static void DeleteBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class ReceiverProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _standardOutput;
    private readonly Task<string> _standardError;
    private int _disposed;

    internal ReceiverProcess(Process process)
    {
        _process = process;
        _standardOutput = process.StandardOutput.ReadToEndAsync();
        _standardError = process.StandardError.ReadToEndAsync();
    }

    internal async Task WaitUntilReadyAsync(Uri operatorEndpoint, string operatorToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", operatorToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Receiver test host exited with {_process.ExitCode}. " +
                    $"stdout={await _standardOutput} stderr={await _standardError}");
            }
            try
            {
                using var response = await client.GetAsync(
                    new Uri(operatorEndpoint, "/api/custody/health"),
                    timeout.Token);
                if (response.StatusCode == HttpStatusCode.OK) return;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException)
            {
            }
            try
            {
                await Task.Delay(25, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }
        throw new TimeoutException("The receiver test host did not become ready.");
    }

    internal async Task KillTreeAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await KillTreeCoreAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await KillTreeCoreAsync();
        _process.Dispose();
    }

    private async Task KillTreeCoreAsync()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _process.WaitForExitAsync(timeout.Token);
    }
}
