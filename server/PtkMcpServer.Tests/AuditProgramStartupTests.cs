using System.Diagnostics;
using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditProgramStartupTests : IDisposable
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
    public async Task Unwritable_audit_root_does_not_block_invoke_and_state_reports_disabled()
    {
        var auditRoot = NewBlockedAuditRoot();
        using var process = StartServer(auditRoot);
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await SendAsync(
                process,
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"audit-independent-startup-test","version":"1"}}}""");
            _ = await ReadResponseAsync(process, 1);
            await SendAsync(
                process,
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await SendAsync(
                process,
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"ptk_invoke","arguments":{"script":"'audit-independent-effect'","route":"pwsh"}}}""");
            var invoke = (await ReadResponseAsync(process, 2)).GetProperty("result");
            Assert.False(
                invoke.TryGetProperty("isError", out var invokeError) &&
                invokeError.GetBoolean());
            Assert.Contains(
                "audit-independent-effect",
                invoke.GetProperty("content")[0].GetProperty("text").GetString(),
                StringComparison.Ordinal);

            await SendAsync(
                process,
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"ptk_state","arguments":{}}}""");
            var state = (await ReadResponseAsync(process, 3)).GetProperty("result");
            Assert.False(
                state.TryGetProperty("isError", out var stateError) &&
                stateError.GetBoolean());
            var text = state.GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Contains("audit: disabled", text, StringComparison.Ordinal);
            Assert.DoesNotContain("audit exporter:", text, StringComparison.Ordinal);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* Preserve the test assertion. */ }
            _ = await stderr;
        }
    }

    private Process StartServer(string auditRoot)
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "PtkMcpServer.dll");
        Assert.True(File.Exists(serverDll), $"server dll not found at {serverDll}");
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(serverDll);
        start.Environment[AuditStartupConfiguration.AuditRootEnvironmentVariable] = auditRoot;
        return Process.Start(start)
            ?? throw new InvalidOperationException("The audit startup test server did not start.");
    }

    private string NewBlockedAuditRoot()
    {
        var root = NewRoot("blocked-audit");
        var blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "not a directory");
        return Path.Combine(blocker, "audit");
    }

    private string NewRoot(string label)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(
            profile,
            ".ptk",
            $"test-{label}-{Guid.NewGuid():N}");
        _roots.Add(root);
        return SecureAuditStorage.PrepareRoot(root);
    }

    private static async Task SendAsync(Process process, string json)
    {
        await process.StandardInput.WriteLineAsync(json);
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonElement> ReadResponseAsync(Process process, int id)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellation.Token)
                ?? throw new InvalidOperationException(
                    $"The server closed stdout while waiting for response {id}.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            var message = JsonSerializer.Deserialize<JsonElement>(line);
            if (message.TryGetProperty("id", out var messageId) &&
                messageId.ValueKind == JsonValueKind.Number &&
                messageId.GetInt32() == id)
            {
                return message;
            }
        }
    }
}
