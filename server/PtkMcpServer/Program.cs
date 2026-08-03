using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PtkMcpServer;
using PtkMcpServer.Sessions;
using PtkMcpServer.Worker;

// Worker classification is the first executable action. An internal worker
// attempt must never enter supervisor host, audit, output, or MCP startup.
if (WorkerProcessEntry.IsWorkerInvocation(args))
{
    Environment.ExitCode = await WorkerProcessEntry.RunAsync(args).ConfigureAwait(false);
    return;
}

// RTK is a required dependency: PTK compresses PowerShell objects itself and
// routes everything else to RTK. Refuse to start rather than come up as a
// half-working server whose native output is silently unfiltered. stderr,
// never stdout — stdout is the JSON-RPC transport.
if (RtkDependency.ResolveExecutablePath() is null)
{
    await Console.Error.WriteLineAsync(RtkDependency.UnavailableMessage())
        .ConfigureAwait(false);
    Environment.ExitCode = 78; // EX_CONFIG
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the JSON-RPC transport; every log line must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var callTimeout = DefaultSessionRuntimeFactory.ReadCallTimeout();
var maxCallTimeout = DefaultSessionRuntimeFactory.ReadMaxCallTimeout();

// One supervisor owns this MCP connection's bounded worker/session registry.
// Submitted scripts execute only in contained worker processes.
builder.Services.AddSingleton(_ => new OutputStore(OutputStoreOptions.Production()));
builder.Services.AddSingleton(_ =>
    WorkerSupervisor.CreateDefault(callTimeout, maxCallTimeout));
builder.Services.AddSingleton<ISessionOperations>(
    sp => sp.GetRequiredService<WorkerSupervisor>());
builder.Services.AddSingleton(sp => new SupervisorLifecycle(
    sp.GetRequiredService<WorkerSupervisor>()));
builder.Services.AddSingleton<IHostedService>(
    sp => sp.GetRequiredService<SupervisorLifecycle>());
// Capture the transport streams BEFORE detaching stdin: the streams wrap the
// original handles, so the JSON-RPC channel keeps working while every child
// process spawned from the warm runspace inherits NUL/EOF instead of the live
// pipe (see ChildStdinGuard).
var mcpIn = Console.OpenStandardInput();
var mcpOut = Console.OpenStandardOutput();
PtkMcpServer.ChildStdinGuard.DetachChildStdin();
builder.Services
    .AddMcpServer(options => options.ScopeRequests = true)
    .WithStreamServerTransport(mcpIn, mcpOut)
    .WithRequestFilters(filters =>
        filters.AddCallToolFilter(SupervisorCallFilter.Create()))
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
