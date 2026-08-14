using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PtkSiemReceiver.Tests;

/// <summary>
/// audit-restoration R5 / mini-SIEM S4: producer-to-receiver conformance
/// over the PRODUCER-OWNED golden request corpora
/// (server/PtkMcpServer.Tests/SiemConformance/*.golden.json — the exact
/// bytes PTK's exporter emits, captured through its real delivery path).
/// These tests fail closed when the fixtures are absent: the receiver never
/// substitutes its own copy of the producer's shape, because the
/// receiver-authored envelope helpers are exactly the drift this suite
/// exists to catch.
/// </summary>
[Collection(SiemReceiverProcessCollection.Name)]
public sealed class ProducerConformanceTests
{
    private const string IngestToken = "conformance-token-0123456789abcdef";

    [Theory]
    [InlineData("otlp-http-v1.golden.json")]
    [InlineData("otlp-http-v2.golden.json")]
    [InlineData("otlp-http-v4.golden.json")]
    public async Task The_exact_producer_golden_request_is_accepted_stored_and_idempotent(
        string goldenName)
    {
        var golden = ProducerGoldenBytes(goldenName);
        var deliveredBodies = LogRecordBodies(golden);
        Assert.Equal(3, deliveredBodies.Count);

        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            ingestToken: IngestToken);
        using var client = host.CreateClient();

        using (var response = await client.SendAsync(GoldenRequest(host.Endpoint, golden)))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(3L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM events;"));
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM quarantine;"));
        Assert.Equal(
            3L,
            DatabaseInt64(
                host.DatabasePath,
                "SELECT head_sequence FROM chains;"));

        // Storage fidelity: every stored evidence body is byte-identical to
        // the body the producer delivered, in sequence order — Unicode
        // escapes and all.
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            var stored = DatabaseBlob(
                host.DatabasePath,
                $"SELECT exact_json_body FROM events WHERE sequence = {sequence};");
            Assert.Equal(Encoding.UTF8.GetBytes(deliveredBodies[sequence - 1]), stored);
        }

        // Value-level Unicode fidelity: the third record's declared purpose
        // decodes from the STORE to the same string it decodes to from the
        // producer's own request bytes.
        Assert.Equal(
            DeclaredPurposeOf(deliveredBodies[2]),
            DeclaredPurposeOf(
                Encoding.UTF8.GetString(DatabaseBlob(
                    host.DatabasePath,
                    "SELECT exact_json_body FROM events WHERE sequence = 3;"))));
        Assert.Contains("検証", DeclaredPurposeOf(deliveredBodies[2]), StringComparison.Ordinal);

        // Timestamp fidelity: the extracted occurred instant is the corpus
        // constant, not a reparse artifact.
        Assert.StartsWith(
            "2026-07-11T12:34:56.123",
            DatabaseString(
                host.DatabasePath,
                "SELECT DISTINCT occurred_utc FROM events;"),
            StringComparison.Ordinal);

        // The producer redelivers at-least-once: replaying the identical
        // golden request must be a no-op, never a quarantine.
        using (var replay = await client.SendAsync(GoldenRequest(host.Endpoint, golden)))
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(3L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM events;"));
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM quarantine;"));
    }

    [Fact]
    public void The_fixture_gate_fails_closed_when_the_producer_corpus_is_absent()
    {
        // The locator must throw a message naming the producer ownership
        // rule rather than quietly skipping — a conformance suite that can
        // pass without its fixtures is not a gate.
        var exception = Record.Exception(
            () => ProducerGoldenBytes("no-such-corpus.golden.json"));
        Assert.NotNull(exception);
        Assert.Contains("producer-owned", exception!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_locator_refuses_an_ancestor_checkouts_corpus()
    {
        // cr6-1: "found somewhere up the tree" is not "found in THIS
        // checkout" — a same-named corpus above the repository boundary
        // must not stand in for a missing local one.
        var outer = Directory.CreateTempSubdirectory("ptk-conformance-locator-");
        try
        {
            var staleDirectory = Directory.CreateDirectory(Path.Combine(
                outer.FullName, "server", "PtkMcpServer.Tests", "SiemConformance"));
            File.WriteAllText(Path.Combine(staleDirectory.FullName, "x.golden.json"), "{}");
            var repo = Directory.CreateDirectory(Path.Combine(outer.FullName, "repo"));
            Directory.CreateDirectory(Path.Combine(repo.FullName, ".git"));
            var start = Directory.CreateDirectory(
                Path.Combine(repo.FullName, "bin", "Debug")).FullName;

            var exception = Assert.Throws<FileNotFoundException>(
                () => ProducerGoldenBytes("x.golden.json", start));
            Assert.Contains("fails closed", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            outer.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_locator_finds_the_corpus_inside_its_own_repository_root()
    {
        var outer = Directory.CreateTempSubdirectory("ptk-conformance-locator-");
        try
        {
            var repo = Directory.CreateDirectory(Path.Combine(outer.FullName, "repo"));
            Directory.CreateDirectory(Path.Combine(repo.FullName, ".git"));
            var fixtures = Directory.CreateDirectory(Path.Combine(
                repo.FullName, "server", "PtkMcpServer.Tests", "SiemConformance"));
            File.WriteAllText(Path.Combine(fixtures.FullName, "x.golden.json"), "{\"ok\":1}");
            var start = Directory.CreateDirectory(
                Path.Combine(repo.FullName, "bin", "Debug")).FullName;

            Assert.Equal(
                "{\"ok\":1}",
                Encoding.UTF8.GetString(ProducerGoldenBytes("x.golden.json", start)));
        }
        finally
        {
            outer.Delete(recursive: true);
        }
    }

    // ---- Helpers ----

    private static HttpRequestMessage GoldenRequest(Uri endpoint, byte[] golden)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(golden),
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", IngestToken);
        return request;
    }

    /// <summary>Locates the producer-owned corpus INSIDE this checkout;
    /// absent fixtures FAIL the suite (S4 fixture gate) — the receiver never
    /// authors a substitute, and the walk stops at the first repository root
    /// so an ancestor checkout's same-named corpus can never stand in for a
    /// missing local one (cr6-1).</summary>
    private static byte[] ProducerGoldenBytes(string name) =>
        ProducerGoldenBytes(name, AppContext.BaseDirectory);

    internal static byte[] ProducerGoldenBytes(string name, string startDirectory)
    {
        for (var current = new DirectoryInfo(startDirectory);
             current is not null;
             current = current.Parent)
        {
            // A `.git` directory (ordinary clone) or file (worktree) marks
            // this checkout's boundary: the corpus lives beneath it or the
            // gate fails closed, never resolving above it.
            var gitEntry = Path.Combine(current.FullName, ".git");
            if (!Directory.Exists(gitEntry) && !File.Exists(gitEntry)) continue;
            var candidate = Path.Combine(
                current.FullName,
                "server",
                "PtkMcpServer.Tests",
                "SiemConformance",
                name);
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
            throw new FileNotFoundException(
                $"The producer-owned golden fixture is missing at '{candidate}'. The S4 " +
                "fixture gate fails closed: generate the corpus on the producer " +
                "(server/PtkMcpServer.Tests, PTK_WRITE_GOLDEN=1) — the receiver never " +
                "substitutes its own copy, wherever else one exists.");
        }
        throw new FileNotFoundException(
            $"No repository root was found above '{startDirectory}', so the " +
            $"producer-owned golden fixture '{name}' cannot be located. The S4 fixture " +
            "gate fails closed.");
    }

    private static IReadOnlyList<string> LogRecordBodies(byte[] golden)
    {
        using var document = JsonDocument.Parse(golden);
        var bodies = new List<string>();
        foreach (var resource in document.RootElement.GetProperty("resourceLogs").EnumerateArray())
        foreach (var scope in resource.GetProperty("scopeLogs").EnumerateArray())
        foreach (var record in scope.GetProperty("logRecords").EnumerateArray())
        {
            bodies.Add(record.GetProperty("body").GetProperty("stringValue").GetString()!);
        }
        return bodies;
    }

    private static string? DeclaredPurposeOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement
            .GetProperty("session")
            .GetProperty("declared_purpose")
            .GetString();
    }

    private static long DatabaseInt64(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar() ?? throw new InvalidOperationException(sql));
    }

    private static string DatabaseString(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(command.ExecuteScalar() ?? throw new InvalidOperationException(sql));
    }

    private static byte[] DatabaseBlob(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (byte[])(command.ExecuteScalar() ?? throw new InvalidOperationException(sql));
    }
}
