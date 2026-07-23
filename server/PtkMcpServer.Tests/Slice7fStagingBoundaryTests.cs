using System.Diagnostics;
using System.Reflection;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class Slice7fStagingBoundaryTests
{
    [Fact]
    public void Operation_transport_remains_unwired_from_production()
    {
        var root = FindRepositoryRoot();
        var productionRoot = Path.Combine(root, "server", "PtkMcpServer");
        var stagingFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.GetFullPath(Path.Combine(
                productionRoot,
                "Worker",
                "WorkerOperationProtocol.cs")),
            Path.GetFullPath(Path.Combine(
                productionRoot,
                "Worker",
                "WorkerOperationScheduler.cs")),
            Path.GetFullPath(Path.Combine(
                productionRoot,
                "Worker",
                "WorkerSessionOperationCodec.cs")),
            Path.GetFullPath(Path.Combine(
                productionRoot,
                "Worker",
                "WorkerPreparedOperationCodec.cs")),
            Path.GetFullPath(Path.Combine(
                productionRoot,
                "Worker",
                "WorkerPreparedInvokeController.cs")),
        };
        foreach (var path in Directory.EnumerateFiles(
            productionRoot,
            "*.cs",
            SearchOption.AllDirectories))
        {
            if (stagingFiles.Contains(Path.GetFullPath(path))) continue;
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("WorkerOperationScheduler", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IWorkerOperationExecutor", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkerOperationProtocol", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkerSessionOperationCodec", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkerSessionOperationArguments", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkerPreparedOperationCodec", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkerInvokePreparePayload", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkerPreparedCorrelation", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkerCommitPayload", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkerAbortPayload", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkerPreparedCorrelationMatch", source, StringComparison.Ordinal);
        }
        foreach (var path in stagingFiles)
        {
            var source = File.ReadAllText(path);
            foreach (var forbidden in new[]
            {
                "ISessionOperations",
                "ISessionLifetime",
                "SessionRuntime",
                "AuditCallContext",
                "OutputStore",
                "RunspaceHost",
                "JobManager",
            })
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
            }
        }

        var program = File.ReadAllText(Path.Combine(
            root,
            "server",
            "PtkMcpServer",
            "Program.cs"));
        Assert.Contains("AddSingleton<ISessionOperations>", program, StringComparison.Ordinal);
        Assert.Contains("DefaultSessionRuntimeFactory.Create", program, StringComparison.Ordinal);

        var concreteCodec = File.ReadAllText(Path.Combine(
            productionRoot,
            "Worker",
            "WorkerSessionOperationCodec.cs"));
        foreach (var forbidden in new[]
        {
            "WorkerOperationRequest",
            "WorkerOperationScheduler",
            "WorkerMessageKind",
            "WorkerServer",
        })
        {
            Assert.DoesNotContain(forbidden, concreteCodec, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(
            typeof(WorkerOperationScheduler).Assembly.GetTypes(),
            type => !type.IsAbstract &&
                !type.IsInterface &&
                typeof(IWorkerOperationExecutor).IsAssignableFrom(type));

        var preparedCodec = File.ReadAllText(Path.Combine(
            productionRoot,
            "Worker",
            "WorkerPreparedOperationCodec.cs"));
        foreach (var forbidden in new[]
        {
            "WorkerEnvelope",
            "WorkerMessageKind",
            "WorkerOperationRequest",
            "WorkerOperationScheduler",
            "IWorkerOperationExecutor",
            "WorkerServer",
            "IServiceProvider",
            "System.Diagnostics",
            "ProcessStartInfo",
            "WindowsProcessTreeSupervisor",
            "IWindowsProcessHandle",
            "IWindowsWorkerNative",
            "WorkerLaunchCommand",
            "ContainedWindowsWorker",
        })
        {
            Assert.DoesNotContain(forbidden, preparedCodec, StringComparison.Ordinal);
        }
        var digestMethod = SourceBlock(
            preparedCodec,
            "private static string ComputeScriptDigest");
        var digestFinallyBlocks = FinallyBlocks(digestMethod);
        Assert.Contains(
            digestFinallyBlocks,
            block => block.Contains(
                    "CryptographicOperations.ZeroMemory(bytes.AsSpan(0, byteCount));",
                    StringComparison.Ordinal) &&
                block.Contains(
                    "ArrayPool<byte>.Shared.Return(bytes, clearArray: true);",
                    StringComparison.Ordinal));
        Assert.Contains(
            digestFinallyBlocks,
            block => block.Contains(
                "CryptographicOperations.ZeroMemory(hash);",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Operation_transport_type_graph_has_no_supervisor_or_runtime_capability()
    {
        var surface = new[]
        {
            typeof(WorkerOperationProtocol),
            typeof(WorkerOperationRequest),
            typeof(WorkerOperationCancel),
            typeof(WorkerOperationResponse),
            typeof(IWorkerOperationExecutor),
            typeof(WorkerOperationScheduler),
            typeof(WorkerSessionOperationCodec),
            typeof(WorkerInvokeRoute),
            typeof(WorkerSessionOperationArguments),
            typeof(WorkerInvokeArguments),
            typeof(WorkerJobListArguments),
            typeof(WorkerJobStatusArguments),
            typeof(WorkerJobOutputArguments),
            typeof(WorkerJobKillArguments),
            typeof(WorkerStateArguments),
            typeof(WorkerSessionOperationResult),
            typeof(WorkerInvokeResult),
            typeof(WorkerJobListResult),
            typeof(WorkerJobStatusResult),
            typeof(WorkerJobOutputResult),
            typeof(WorkerJobKillResult),
            typeof(WorkerStateResult),
            typeof(WorkerPreparedOperationCodec),
            typeof(WorkerInvokePreparePayload),
            typeof(WorkerPreparedCorrelation),
            typeof(WorkerCommitPayload),
            typeof(WorkerAbortPayload),
            typeof(WorkerPreparedCorrelationMatch),
            typeof(WorkerPreparedPlanDescriptor),
            typeof(WorkerPreparedRuntimeResult),
            typeof(IWorkerPreparedInvokeRuntime),
            typeof(IWorkerPreparedInvokeObserver),
            typeof(WorkerPreparedInvokeTerminalKind),
            typeof(WorkerPreparedInvokeTerminal),
            typeof(WorkerPreparedInvokeController),
        }
        .SelectMany(type => type
            .GetMembers(BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(MemberTypes))
        .SelectMany(Flatten)
        .ToHashSet();

        foreach (var forbidden in new[]
        {
            typeof(ISessionOperations),
            typeof(ISessionLifetime),
            typeof(SessionRuntime),
            typeof(AuditCallContext),
            typeof(AuditCallContextAccessor),
            typeof(OutputStore),
            typeof(RunspaceHost),
            typeof(JobManager),
            typeof(IServiceProvider),
            typeof(Process),
            typeof(ProcessStartInfo),
            typeof(WindowsProcessTreeSupervisor),
            typeof(IWindowsProcessHandle),
            typeof(IWindowsWorkerNative),
            typeof(WorkerLaunchCommand),
            typeof(ContainedWindowsWorker),
        })
        {
            Assert.DoesNotContain(forbidden, surface);
        }

        static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
        {
            FieldInfo field => [field.FieldType],
            PropertyInfo property => [property.PropertyType],
            MethodInfo method =>
                [method.ReturnType, .. method.GetParameters().Select(parameter => parameter.ParameterType)],
            ConstructorInfo constructor =>
                constructor.GetParameters().Select(parameter => parameter.ParameterType),
            _ => [],
        };

        static IEnumerable<Type> Flatten(Type type)
        {
            yield return type;
            if (type.HasElementType && type.GetElementType() is { } element)
            {
                foreach (var nested in Flatten(element)) yield return nested;
            }
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var nested in Flatten(argument)) yield return nested;
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string SourceBlock(string source, string declaration)
    {
        var declarationOffset = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(declarationOffset >= 0);
        var openOffset = source.IndexOf('{', declarationOffset);
        Assert.True(openOffset >= 0);
        var closeOffset = MatchingBrace(source, openOffset);
        return source[openOffset..(closeOffset + 1)];
    }

    private static string[] FinallyBlocks(string source)
    {
        var blocks = new List<string>();
        var searchOffset = 0;
        while (true)
        {
            var finallyOffset = source.IndexOf(
                "finally",
                searchOffset,
                StringComparison.Ordinal);
            if (finallyOffset < 0) return blocks.ToArray();
            var openOffset = source.IndexOf('{', finallyOffset);
            Assert.True(openOffset >= 0);
            var closeOffset = MatchingBrace(source, openOffset);
            blocks.Add(source[openOffset..(closeOffset + 1)]);
            searchOffset = closeOffset + 1;
        }
    }

    private static int MatchingBrace(string source, int openOffset)
    {
        var depth = 0;
        for (var offset = openOffset; offset < source.Length; offset++)
        {
            depth += source[offset] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };
            if (depth == 0) return offset;
        }

        throw new InvalidOperationException("Source block has no matching close brace.");
    }
}
