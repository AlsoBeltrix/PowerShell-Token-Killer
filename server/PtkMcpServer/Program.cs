using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the JSON-RPC transport; every log line must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var callTimeout = TimeSpan.FromSeconds(
    double.TryParse(Environment.GetEnvironmentVariable("PTK_CALL_TIMEOUT_SECONDS"), out var s) && s > 0
        ? s
        : 300);

builder.Services.AddSingleton(new PtkMcpServer.RunspaceHost(callTimeout));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
