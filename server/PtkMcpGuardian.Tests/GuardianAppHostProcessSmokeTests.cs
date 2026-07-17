using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianAppHostProcessSmokeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] ExpectedToolNames =
    [
        "ptk_invoke",
        "ptk_job",
        "ptk_output",
        "ptk_reset",
        "ptk_session",
        "ptk_state",
    ];

    [Fact]
    public async Task Apphost_serves_one_clean_MCP_connection_and_exits_on_input_eof()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        await using var host = StartedAppHost.Start(["--fake-host"]);
        var publicLines = new List<string>();
        var sentMethods = new List<string>();

        var initialized = await SendRequestAsync(
            host,
            publicLines,
            requestId: 1,
            method: "initialize",
            parameters: new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new
                {
                    name = "guardian-apphost-process-smoke",
                    version = "1.0.0",
                },
            },
            sentMethods,
            timeout.Token);
        Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());

        await SendNotificationAsync(
            host,
            method: "notifications/initialized",
            parameters: new { },
            sentMethods,
            timeout.Token);

        var listed = await SendRequestAsync(
            host,
            publicLines,
            requestId: 2,
            method: "tools/list",
            parameters: new { },
            sentMethods,
            timeout.Token);
        var toolNames = listed
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToArray();
        Assert.Equal(ExpectedToolNames, toolNames);

        var jobs = await SendRequestAsync(
            host,
            publicLines,
            requestId: 3,
            method: "tools/call",
            parameters: new
            {
                name = "ptk_job",
                arguments = new { action = "list" },
            },
            sentMethods,
            timeout.Token);
        using (var document = JsonDocument.Parse(AssertToolSuccess(jobs)))
            Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);

        var stateResponse = await SendRequestAsync(
            host,
            publicLines,
            requestId: 4,
            method: "tools/call",
            parameters: new
            {
                name = "ptk_state",
                arguments = new { },
            },
            sentMethods,
            timeout.Token);
        var state = PublicStateCodec.Decode(
            Encoding.UTF8.GetBytes(AssertToolSuccess(stateResponse)));
        Assert.Equal(PublicHostState.Ready, state.Host.State);
        Assert.True(state.Host.ReadyForEffects);

        Assert.Equal(1, sentMethods.Count(method => method == "initialize"));
        Assert.Equal(
            1,
            sentMethods.Count(method => method == "notifications/initialized"));

        await host.CloseInputAsync();
        var stdoutTail = DrainPublicOutputAsync(
            host.StandardOutput,
            publicLines,
            timeout.Token);
        using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        exitTimeout.CancelAfter(ProcessExitTimeout);
        Assert.Equal(0, await host.WaitForExitAsync(exitTimeout.Token));
        await stdoutTail;

        Assert.Equal(string.Empty, await host.ReadStandardErrorAsync(timeout.Token));
        Assert.Equal(4, publicLines.Count);
        Assert.All(publicLines, line =>
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
            Assert.True(root.TryGetProperty("id", out _), line);
            Assert.False(root.TryGetProperty("method", out _), line);
        });
    }

    [Fact]
    public async Task Apphost_rejects_relaxed_fake_host_flag_without_opening_stdout()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        await using var host = StartedAppHost.Start(["--fake-host", "extra"]);
        await host.CloseInputAsync();
        var stdout = host.StandardOutput.ReadToEndAsync(timeout.Token);

        using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        exitTimeout.CancelAfter(ProcessExitTimeout);
        Assert.Equal(Program.UsageExitCode, await host.WaitForExitAsync(exitTimeout.Token));
        Assert.Equal(string.Empty, await stdout);

        var standardError = await host.ReadStandardErrorAsync(timeout.Token);
        Assert.Equal(
            Program.UnsupportedModeMessage + Environment.NewLine,
            standardError);
        Assert.InRange(Encoding.UTF8.GetByteCount(standardError), 1, 1_024);
    }

    private static async Task<JsonElement> SendRequestAsync(
        StartedAppHost host,
        List<string> publicLines,
        int requestId,
        string method,
        object parameters,
        List<string> sentMethods,
        CancellationToken cancellationToken)
    {
        sentMethods.Add(method);
        await WriteMessageAsync(
            host.StandardInput,
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["method"] = method,
                ["params"] = parameters,
            },
            cancellationToken);

        while (true)
        {
            var line = await host.StandardOutput.ReadLineAsync(cancellationToken);
            Assert.NotNull(line);
            publicLines.Add(line);
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            Assert.Equal(JsonValueKind.Object, message.ValueKind);
            Assert.Equal("2.0", message.GetProperty("jsonrpc").GetString());
            if (message.TryGetProperty("id", out var responseId) &&
                responseId.ValueKind == JsonValueKind.Number &&
                responseId.GetInt32() == requestId)
            {
                return message.Clone();
            }
        }
    }

    private static Task SendNotificationAsync(
        StartedAppHost host,
        string method,
        object parameters,
        List<string> sentMethods,
        CancellationToken cancellationToken)
    {
        sentMethods.Add(method);
        return WriteMessageAsync(
            host.StandardInput,
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters,
            },
            cancellationToken);
    }

    private static async Task WriteMessageAsync(
        StreamWriter input,
        object message,
        CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(message);
        await input.WriteLineAsync(line.AsMemory(), cancellationToken);
        await input.FlushAsync(cancellationToken);
    }

    private static async Task DrainPublicOutputAsync(
        StreamReader output,
        List<string> publicLines,
        CancellationToken cancellationToken)
    {
        while (await output.ReadLineAsync(cancellationToken) is { } line)
        {
            publicLines.Add(line);
            using var document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.Equal(
                "2.0",
                document.RootElement.GetProperty("jsonrpc").GetString());
        }
    }

    private static string AssertToolSuccess(JsonElement response)
    {
        var result = response.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        return Assert.IsType<string>(content.GetProperty("text").GetString());
    }

    private sealed class StartedAppHost : IAsyncDisposable
    {
        private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

        private readonly Process _process;
        private readonly Task<string> _standardError;
        private bool _inputClosed;

        private StartedAppHost(Process process)
        {
            _process = process;
            _process.StandardInput.NewLine = "\n";
            _standardError = _process.StandardError.ReadToEndAsync();
        }

        internal StreamWriter StandardInput => _process.StandardInput;
        internal StreamReader StandardOutput => _process.StandardOutput;

        internal static StartedAppHost Start(IReadOnlyList<string> arguments)
        {
            var executableName = OperatingSystem.IsWindows()
                ? "PtkMcpGuardian.exe"
                : "PtkMcpGuardian";
            var executablePath = Path.Combine(AppContext.BaseDirectory, executableName);
            Assert.True(
                File.Exists(executablePath),
                $"Guardian apphost was not built at '{executablePath}'.");

            var start = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);

            var process = new Process { StartInfo = start };
            try
            {
                Assert.True(process.Start(), "Guardian apphost did not start.");
                return new StartedAppHost(process);
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        internal async Task CloseInputAsync()
        {
            if (_inputClosed)
                return;
            await _process.StandardInput.DisposeAsync();
            _inputClosed = true;
        }

        internal async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken);
            return _process.ExitCode;
        }

        internal Task<string> ReadStandardErrorAsync(CancellationToken cancellationToken) =>
            _standardError.WaitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                try
                {
                    await CloseInputAsync();
                }
                catch (Exception exception) when (
                    exception is IOException or ObjectDisposedException or InvalidOperationException)
                {
                }

                if (!HasExited())
                {
                    try
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException or NotSupportedException or
                            Win32Exception)
                    {
                    }
                }

                using var cleanup = new CancellationTokenSource(CleanupTimeout);
                if (!HasExited())
                {
                    try
                    {
                        await _process.WaitForExitAsync(cleanup.Token);
                    }
                    catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                    {
                    }
                }
                try
                {
                    _ = await _standardError.WaitAsync(cleanup.Token);
                }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                {
                }
            }
            finally
            {
                _process.Dispose();
            }
        }

        private bool HasExited()
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }
}
