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

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the JSON-RPC transport; every log line must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var callTimeout = DefaultSessionRuntimeFactory.ReadCallTimeout();
var maxCallTimeout = DefaultSessionRuntimeFactory.ReadMaxCallTimeout();
// Resolve once before serving. Warm-session scripts can mutate the process
// PATH, but background jobs must keep using the executable selected at server
// startup. A failed lookup is also frozen so a later PATH cannot supply one.
var jobPwshExecutable = JobPwshExecutable.ResolveFromPath();

// Harness-lifetime recovery belongs to the supervisor service provider, not
// a request scope or the replaceable runspace host.
builder.Services.AddSingleton(_ => new OutputStore(OutputStoreOptions.Production()));
builder.Services.AddSingleton(_ => new WorkerSupervisor(() =>
    DefaultSessionRuntimeFactory.Create(
        callTimeout,
        maxCallTimeout,
        jobPwshExecutable)));
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
