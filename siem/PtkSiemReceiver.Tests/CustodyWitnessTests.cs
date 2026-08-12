using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PtkSiemReceiver.Ingest;
using PtkSiemReceiver.Security;
using PtkSiemReceiver.Storage;

namespace PtkSiemReceiver.Tests;

[Collection(SiemReceiverProcessCollection.Name)]
public sealed class CustodyWitnessTests
{
    [Fact]
    public async Task Checkpoints_are_protected_and_copied_to_the_write_once_anchor()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        var anchorRoot = SiemTestFileSystem.CreateProtectedRoot("ptk-siem-anchor");
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            existingAnchorRoot: anchorRoot);
        using var client = host.CreateClient(clientCertificate);

        using var response = await client.PostAsync(
            host.Endpoint, OtlpTestRequest.Content());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = await host.CustodyWitness.CheckpointAsync(
            force: true, CancellationToken.None);

        Assert.True(health.Healthy);
        Assert.True(health.AnchorConfigured);
        var witnessFiles = Directory.GetFiles(host.WitnessRoot)
            .Order(StringComparer.Ordinal).ToArray();
        var anchorFiles = Directory.GetFiles(anchorRoot)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(witnessFiles.Select(Path.GetFileName), anchorFiles.Select(Path.GetFileName));
        Assert.Equal(witnessFiles.Length, anchorFiles.Length);
        for (var index = 0; index < witnessFiles.Length; index++)
            Assert.Equal(File.ReadAllBytes(witnessFiles[index]), File.ReadAllBytes(anchorFiles[index]));
        Assert.All(witnessFiles, AssertOwnerOnlyFile);
        Assert.All(anchorFiles, AssertOwnerOnlyFile);

        File.Delete(anchorFiles[^1]);
        var regressed = await host.CustodyWitness.CheckpointAsync(
            force: true, CancellationToken.None);
        Assert.False(regressed.Healthy);
        Assert.Equal("custody_witness_check", regressed.FailureCode);
    }

    [Fact]
    public async Task Periodic_check_refuses_ingest_after_a_rehashed_checkpoint_regression()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        await using var host = await SiemReceiverTestHost.StartAsync(server, [root]);
        using var client = host.CreateClient(clientCertificate);
        using (var accepted = await client.PostAsync(
                   host.Endpoint, OtlpTestRequest.Content()))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        _ = await host.CustodyWitness.CheckpointAsync(
            force: true, CancellationToken.None);
        RewriteLatestCheckpointAsRegression(host.WitnessRoot);

        var health = await host.CustodyWitness.CheckpointAsync(
            force: true, CancellationToken.None);
        using var refused = await client.PostAsync(
            host.Endpoint,
            OtlpTestRequest.Content(OtlpTestRequest.Create(
                eventId: "018f6a78-4c20-7a11-8a34-1234567890ab")));

        Assert.False(health.Healthy);
        Assert.Equal("custody_witness_check", health.FailureCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
    }

    [Fact]
    public async Task Older_backup_restore_requires_operator_authorization_and_alerts_once()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        string dataRoot;
        string witnessRoot;
        string databasePath;
        string backupPath;
        string firstHash;

        await using (var firstHost = await SiemReceiverTestHost.StartAsync(
                         server,
                         [root],
                         preserveRootOnDispose: true,
                         preserveWitnessOnDispose: true))
        {
            dataRoot = firstHost.Root;
            witnessRoot = firstHost.WitnessRoot;
            databasePath = firstHost.DatabasePath;
            using var client = firstHost.CreateClient(clientCertificate);
            var first = OtlpTestRequest.Create();
            firstHash = Assert.IsType<ValidatedOtlpRecord>(
                OtlpRequestValidator.Validate(first.ToByteArray()).Record).EventHash;
            using var response = await client.PostAsync(
                firstHost.Endpoint, OtlpTestRequest.Content(first));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            _ = await firstHost.CustodyWitness.CheckpointAsync(
                force: true, CancellationToken.None);
            backupPath = Path.Combine(dataRoot, "older-backup.db");
            CreateOnlineBackup(databasePath, backupPath);
        }

        var second = OtlpTestRequest.Create(
            eventId: "018f6a78-4c20-7a11-8a34-1234567890ac",
            sequence: 2,
            previousEventHash: firstHash);
        await using (var secondHost = await SiemReceiverTestHost.StartAsync(
                         server,
                         [root],
                         existingRoot: dataRoot,
                         preserveRootOnDispose: true,
                         existingWitnessRoot: witnessRoot,
                         preserveWitnessOnDispose: true))
        {
            using var client = secondHost.CreateClient(clientCertificate);
            using var response = await client.PostAsync(
                secondHost.Endpoint, OtlpTestRequest.Content(second));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            _ = await secondHost.CustodyWitness.CheckpointAsync(
                force: true, CancellationToken.None);
        }

        RestoreDatabaseFile(backupPath, databasePath);
        await using (var restoredHost = await SiemReceiverTestHost.StartAsync(
                         server,
                         [root],
                         existingRoot: dataRoot,
                         preserveRootOnDispose: true,
                         existingWitnessRoot: witnessRoot,
                         preserveWitnessOnDispose: true))
        {
            using var ingestClient = restoredHost.CreateClient(clientCertificate);
            using (var refused = await ingestClient.PostAsync(
                       restoredHost.Endpoint, OtlpTestRequest.Content(second)))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            }

            using var operatorClient = new HttpClient { BaseAddress = restoredHost.OperatorEndpoint };
            using (var unauthorized = await operatorClient.PostAsync(
                       "api/custody/restore", content: null))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                Assert.Equal(0L, Scalar(
                    databasePath, "SELECT COUNT(*) FROM custody_restore_events;"));
            }
            operatorClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", SiemReceiverTestHost.OperatorToken);
            using var healthResponse = await operatorClient.GetAsync("api/custody/health");
            using var healthJson = JsonDocument.Parse(
                await healthResponse.Content.ReadAsByteArrayAsync());
            Assert.True(healthJson.RootElement.GetProperty("restore_pending").GetBoolean());

            using var restoreResponse = await operatorClient.PostAsync(
                "api/custody/restore", content: null);
            Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);
            using var duplicate = await operatorClient.PostAsync(
                "api/custody/restore", content: null);
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

            Assert.Equal(1L, Scalar(
                databasePath, "SELECT COUNT(*) FROM custody_restore_events;"));
            Assert.Equal(1L, Scalar(
                databasePath,
                "SELECT COUNT(*) FROM alerts WHERE rule_name = 'custody_restore_data_loss' AND state = 'open';"));
            Assert.True((await restoredHost.CustodyWitness.CheckpointAsync(
                force: true, CancellationToken.None)).Healthy);

            using var replay = await ingestClient.PostAsync(
                restoredHost.Endpoint, OtlpTestRequest.Content(second));
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        await using (var reopenedHost = await SiemReceiverTestHost.StartAsync(
                         server,
                         [root],
                         existingRoot: dataRoot,
                         preserveRootOnDispose: true,
                         existingWitnessRoot: witnessRoot,
                         preserveWitnessOnDispose: true))
        {
            Assert.True((await reopenedHost.CustodyWitness.CheckpointAsync(
                force: true, CancellationToken.None)).Healthy);
            Assert.Equal(1L, Scalar(
                databasePath, "SELECT COUNT(*) FROM custody_restore_events;"));
            Assert.Equal(1L, Scalar(
                databasePath,
                "SELECT COUNT(*) FROM alerts WHERE rule_name = 'custody_restore_data_loss';"));
            Execute(
                databasePath,
                "UPDATE custody_restore_events SET operator_actor = 'forged';");
            var mutated = await reopenedHost.CustodyWitness.CheckpointAsync(
                force: true, CancellationToken.None);
            Assert.False(mutated.Healthy);
            Assert.Equal("custody_integrity_subject", mutated.FailureCode);
        }

        try
        {
            Directory.Delete(dataRoot, recursive: true);
            Directory.Delete(witnessRoot, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort test cleanup.
        }
    }

    private static void RewriteLatestCheckpointAsRegression(string witnessRoot)
    {
        var path = Directory.GetFiles(witnessRoot).Order(StringComparer.Ordinal).Last();
        var node = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        node["custody_sequence"] = 0;
        node["custody_hash"] = null;
        var sequence = node["witness_sequence"]!.GetValue<long>();
        var hash = CustodyWitnessHash.Compute(
            sequence,
            node["previous_witness_hash"]?.GetValue<string>(),
            node["kind"]!.GetValue<string>(),
            node["observed_utc"]!.GetValue<string>(),
            0,
            null,
            node["prior_custody_sequence"]?.GetValue<long>(),
            node["prior_custody_hash"]?.GetValue<string>(),
            node["operator_actor"]?.GetValue<string>(),
            node["operator_endpoint"]?.GetValue<string>());
        node["record_hash"] = hash;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
        File.Delete(path);
        _ = SiemProtectedPath.WriteNewProtectedFile(
            Path.Combine(witnessRoot, $"{sequence:D20}-{hash}.json"), bytes);
    }

    private static void CreateOnlineBackup(string sourcePath, string backupPath)
    {
        using var source = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        using var destination = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        destination.Close();
        _ = SiemProtectedPath.ProtectCreatedFile(backupPath);
    }

    private static void RestoreDatabaseFile(string backupPath, string databasePath)
    {
        File.Copy(backupPath, databasePath, overwrite: true);
        _ = SiemProtectedPath.ProtectCreatedFile(databasePath);
        foreach (var sidecar in new[] { databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
    }

    private static long Scalar(string databasePath, string sql)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
            }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private static void AssertOwnerOnlyFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(SiemProtectedPath.OwnerFileMode, File.GetUnixFileMode(path));
    }
}
