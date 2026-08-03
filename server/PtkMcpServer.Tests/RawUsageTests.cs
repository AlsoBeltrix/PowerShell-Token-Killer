using System.ComponentModel;
using System.Reflection;
using PtkMcpServer.Sessions;
using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

// Deprecated raw compatibility telemetry: raw=true is inert but remains
// counted at the ptk_invoke user boundary and visible in ptk_state until a
// later breaking tool-schema revision removes it.
public sealed class RawUsageTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));
    private readonly RawUsageCounter _rawUsage = new();
    private readonly SessionRuntime _runtime;

    public RawUsageTests()
    {
        _runtime = new SessionRuntime(_host, _rawUsage);
    }

    public void Dispose() => _runtime.Dispose();

    [Fact]
    public async Task User_raw_call_increments_exactly_once_and_surfaces_in_state()
    {
        await _runtime.InvokeAsync("'first'", CancellationToken.None, raw: true);
        Assert.Equal(1, _rawUsage.Count);

        await _runtime.InvokeAsync("'second'", CancellationToken.None, raw: true);
        Assert.Equal(2, _rawUsage.Count);

        var state = await _runtime.StateAsync(
            listAvailable: false,
            CancellationToken.None);
        Assert.Contains("raw calls this session: 2", state);
    }

    [Fact]
    public async Task Raw_call_emits_the_server_log_line()
    {
        var original = Console.Error;
        var capture = new StringWriter();
        Console.SetError(capture);
        try
        {
            await _runtime.InvokeAsync("'logged'", CancellationToken.None, raw: true);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Contains("ptk: raw=true call #1 this session", capture.ToString());
    }

    [Fact]
    public async Task Internal_probes_and_non_raw_calls_never_inflate_the_raw_counter()
    {
        await _runtime.StateAsync(listAvailable: false, CancellationToken.None);
        Assert.Equal(0, _rawUsage.Count);

        var nonRaw = await _runtime.InvokeAsync("'plain'", CancellationToken.None);
        Assert.Contains("plain", nonRaw);
        Assert.Equal(0, _rawUsage.Count);
    }

    [Fact]
    public void Invoke_descriptions_teach_same_invocation_recovery_and_inert_legacy_raw()
    {
        var invoke = typeof(InvokeTool).GetMethod(nameof(InvokeTool.Invoke))!;
        var tool = DescriptionOf(invoke);
        Assert.Contains("token-compressed", tool);
        Assert.Contains("ptk_output handle", tool);
        Assert.Contains("same-invocation", tool);
        Assert.Contains("instead of rerunning", tool);
        Assert.Contains("legacy raw flag", tool);
        Assert.Contains("does not change routing, capture, or shaping", tool);
        Assert.Contains("selected session", tool);

        var rawParam = Parameter(invoke, "raw");
        var raw = DescriptionOf(rawParam);
        Assert.Equal(typeof(bool), rawParam.ParameterType);
        Assert.Equal(false, rawParam.DefaultValue);
        Assert.Contains("Deprecated compatibility flag", raw);
        Assert.Contains(
            "no effect on routing, process choice, capture, or shaping",
            raw);
        Assert.Contains("Use ptk_output when a handle is returned", raw);

        var route = DescriptionOf(Parameter(invoke, "route"));
        Assert.Contains("'pwsh' skips rtk", route);
        Assert.Contains("runs the exact original text as PowerShell", route);

        var session = DescriptionOf(Parameter(invoke, "session"));
        Assert.Contains("Connection-local warm session name", session);
        Assert.Contains("ptk_session", session);
    }

    [Fact]
    public void Tool_adapters_expose_only_the_five_tool_worker_surface()
    {
        var sessionMethods = new[]
        {
            typeof(InvokeTool).GetMethod(nameof(InvokeTool.Invoke))!,
            typeof(StateTool).GetMethod(nameof(StateTool.State))!,
            typeof(ResetTool).GetMethod(nameof(ResetTool.Reset))!,
            typeof(SessionTool).GetMethod(nameof(SessionTool.Session))!,
        };

        foreach (var method in sessionMethods)
        {
            var parameters = method.GetParameters();
            Assert.Single(
                parameters,
                parameter => parameter.ParameterType == typeof(ISessionOperations));
            Assert.DoesNotContain(
                parameters,
                parameter => parameter.ParameterType == typeof(SessionRuntime));
            Assert.DoesNotContain(
                parameters,
                parameter => parameter.ParameterType == typeof(RunspaceHost));
            Assert.DoesNotContain(
                parameters,
                parameter => parameter.ParameterType == typeof(RawUsageCounter));
        }

        Assert.Equal(
            "runtime,script,cancellationToken,raw,route,timeoutSeconds,session,outputStore",
            ParameterNames(sessionMethods[0]));
        Assert.Equal(
            "runtime,listAvailable,session,cancellationToken",
            ParameterNames(sessionMethods[1]));
        Assert.Equal(
            "runtime,session,cancellationToken",
            ParameterNames(sessionMethods[2]));
        Assert.Equal(
            "runtime,action,name,cancellationToken",
            ParameterNames(sessionMethods[3]));

        Assert.Equal(false, Parameter(sessionMethods[0], "raw").DefaultValue);
        Assert.Equal("auto", Parameter(sessionMethods[0], "route").DefaultValue);
        Assert.Equal(0, Parameter(sessionMethods[0], "timeoutSeconds").DefaultValue);
        Assert.Equal(
            NamedSessionSupervisor.DefaultName,
            Parameter(sessionMethods[0], "session").DefaultValue);
        Assert.Equal(
            NamedSessionSupervisor.DefaultName,
            Parameter(sessionMethods[1], "session").DefaultValue);
        Assert.Equal(
            NamedSessionSupervisor.DefaultName,
            Parameter(sessionMethods[2], "session").DefaultValue);
        Assert.Null(Parameter(sessionMethods[3], "name").DefaultValue);

        var output = typeof(OutputTool).GetMethod(nameof(OutputTool.Output))!;
        Assert.DoesNotContain(
            output.GetParameters(),
            parameter => parameter.ParameterType == typeof(ISessionOperations));
        Assert.Single(
            output.GetParameters(),
            parameter => parameter.ParameterType == typeof(OutputStore));
        Assert.Null(Parameter(output, "handle").DefaultValue);
        Assert.Null(Parameter(output, "session").DefaultValue);
    }

    [Fact]
    public void Output_description_teaches_immutable_non_executing_recovery()
    {
        var output = DescriptionOf(
            typeof(OutputTool).GetMethod(nameof(OutputTool.Output))!);
        Assert.Contains("immutable output snapshot", output);
        Assert.Contains("without executing or rerunning anything", output);
        Assert.Contains("action=list discovers up to ten", output);
        Assert.Contains("never starts a session or worker", output);
    }

    private static string DescriptionOf(ICustomAttributeProvider member) =>
        ((DescriptionAttribute)member
            .GetCustomAttributes(typeof(DescriptionAttribute), false)
            .Single()).Description;

    private static ParameterInfo Parameter(MethodInfo method, string name) =>
        method.GetParameters().Single(parameter => parameter.Name == name);

    private static string ParameterNames(MethodInfo method) =>
        string.Join(',', method.GetParameters().Select(parameter => parameter.Name));
}
