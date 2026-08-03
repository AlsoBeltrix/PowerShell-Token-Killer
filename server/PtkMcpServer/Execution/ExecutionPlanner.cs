using System.Collections.Immutable;
using System.Management.Automation;
using System.Management.Automation.Language;

namespace PtkMcpServer;

/// <summary>
/// Pure planner over a captured command snapshot. It never resolves commands,
/// starts a process, or enters the user runspace.
/// </summary>
internal static class ExecutionPlanner
{
    private static readonly HashSet<string> ContextChangingContainerWrappers = new(
        ["docker", "podman", "kubectl", "oc"],
        StringComparer.OrdinalIgnoreCase);

    internal static ExecutionPlan Create(
        string script,
        string? route,
        RtkExecutableIdentity? effectiveRtkIdentity,
        TrustedCommandSnapshot commands,
        bool compressAvailable,
        ResolutionContext resolutionContext,
        string? workingDirectory = null,
        string? nativeArgumentPassing = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(commands);
        if (effectiveRtkIdentity is not null &&
            (string.IsNullOrWhiteSpace(effectiveRtkIdentity.ExecutablePath) ||
             !Path.IsPathFullyQualified(effectiveRtkIdentity.ExecutablePath)))
        {
            effectiveRtkIdentity = null;
        }

        var requestedRoute = NormalizeRoute(route);
        var noFallbacks = ImmutableArray<ExecutionPath>.Empty;
        if (requestedRoute == RequestedExecutionRoute.PowerShell)
        {
            return Direct(
                script,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain: null,
                noFallbacks,
                fallbackReason: null,
                effectiveRtkIdentity);
        }

        var domain = ClassifyDomain(script, commands);
        if (effectiveRtkIdentity is null)
        {
            return Direct(
                script,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                noFallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkExecutableUnavailable
                    : null,
                outputShapingRtkIdentity: null);
        }

        var command = GetEligibleCommand(script);
        if (command is null)
        {
            return Direct(
                script,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                noFallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkIneligibleShape
                    : null,
                effectiveRtkIdentity);
        }

        var name = ((StringConstantExpressionAst)command.CommandElements[0]).Value;
        if (Path.GetFileNameWithoutExtension(name)
            .Equals("rtk", StringComparison.OrdinalIgnoreCase))
        {
            return Direct(
                script,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                noFallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkSelfInvocation
                    : null,
                effectiveRtkIdentity);
        }

        var resolved = commands.Resolve(name, CommandTypes.All);
        if (resolved?.CommandType != CommandTypes.Application)
        {
            return Direct(
                script,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                noFallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkResolutionNotApplication
                    : null,
                effectiveRtkIdentity);
        }
        var extension = Path.GetExtension(resolved.Source ?? string.Empty);
        if (IsAlwaysExcludedLauncherExtension(extension))
        {
            return Direct(
                script,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                noFallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkFidelityExclusion
                    : null,
                effectiveRtkIdentity);
        }
        if (IsContextChangingWrapper(command))
        {
            return Direct(
                script,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                noFallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkFidelityExclusion
                    : null,
                effectiveRtkIdentity);
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) ||
            !Path.IsPathFullyQualified(workingDirectory) ||
            !TryCreateRtkArgumentVector(command, out var rtkArgumentVector) ||
            !SupportsDirectArgumentPassing(
                nativeArgumentPassing,
                resolved.Source,
                rtkArgumentVector,
                allowUnknownModeInvariant:
                    resolutionContext == ResolutionContext.Cold))
        {
            return Direct(
                script,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                noFallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkFidelityExclusion
                    : null,
                effectiveRtkIdentity);
        }

        // Hash only after every cheap eligibility check. Cold planning shares
        // the call deadline, so an ineligible command must not spend it reading
        // a large target that will never be dispatched through RTK.
        var coldTargetIdentity = resolutionContext == ResolutionContext.Cold
            ? ColdCommandTargetIdentity.TryCapture(
                name,
                resolved,
                workingDirectory)
            : null;
        if (resolutionContext == ResolutionContext.Cold && coldTargetIdentity is null)
        {
            return Direct(
                script,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                noFallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkFidelityExclusion
                    : null,
                effectiveRtkIdentity);
        }

        return new ExecutionPlan(
            script,
            executionScript: null,
            domain,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            resolutionContext,
            requestedRoute,
            OutputProvenance.RtkUnknown,
            ImmutableArray.Create(ExecutionPath.PowerShellDirect),
            fallbackReason: null,
            effectiveRtkIdentity,
            workingDirectory: workingDirectory,
            rtkArgumentVector: rtkArgumentVector,
            directFallbackProvenance:
                resolutionContext == ResolutionContext.Cold || !compressAvailable
                    ? OutputProvenance.DirectText
                    : OutputProvenance.PowerShellObjects,
            coldCommandTargetIdentity: coldTargetIdentity);
    }

    internal static ExecutionPlan CreateDirect(
        string script,
        string? route,
        bool compressAvailable,
        ResolutionContext resolutionContext,
        RtkExecutableIdentity? outputShapingRtkIdentity = null) =>
        Direct(
            script,
            compressAvailable,
            resolutionContext,
            NormalizeRoute(route),
            domain: null,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null,
            outputShapingRtkIdentity);

    internal static ExecutionPlan CreateBash(
        string script,
        string? route,
        RtkExecutableIdentity rtkExecutableIdentity,
        BashExecutableIdentity bashExecutableIdentity,
        string workingDirectory,
        ResolutionContext resolutionContext)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(rtkExecutableIdentity);
        ArgumentNullException.ThrowIfNull(bashExecutableIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var requestedRoute = NormalizeRoute(route);
        Parser.ParseInput(script, out _, out var parseErrors);
        if (parseErrors.Length == 0 || requestedRoute == RequestedExecutionRoute.PowerShell)
        {
            throw new ArgumentException(
                "Bash delegation requires independently parse-fatal PowerShell input without route=pwsh consent.",
                nameof(script));
        }

        return new ExecutionPlan(
            script,
            executionScript: null,
            ExecutionDomain.Bash,
            ExecutionPath.BashViaRtk,
            PreExecutionValidation.BashSyntax,
            resolutionContext,
            requestedRoute,
            OutputProvenance.RtkUnknown,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null,
            rtkExecutableIdentity,
            bashExecutableIdentity,
            workingDirectory);
    }

    private static ExecutionPlan Direct(
        string script,
        bool compressAvailable,
        ResolutionContext resolutionContext,
        RequestedExecutionRoute requestedRoute,
        ExecutionDomain? domain,
        ImmutableArray<ExecutionPath> fallbacks,
        ExecutionFallbackReason? fallbackReason,
        RtkExecutableIdentity? outputShapingRtkIdentity) =>
        new(
            script,
            script,
            domain,
            ExecutionPath.PowerShellDirect,
            PreExecutionValidation.None,
            resolutionContext,
            requestedRoute,
            resolutionContext == ResolutionContext.Cold || !compressAvailable
                ? OutputProvenance.DirectText
                : OutputProvenance.PowerShellObjects,
            fallbacks,
            fallbackReason,
            rtkExecutableIdentity: null,
            outputShapingRtkIdentity:
                resolutionContext == ResolutionContext.Cold || !compressAvailable
                ? null
                : outputShapingRtkIdentity);

    private static RequestedExecutionRoute NormalizeRoute(string? route) =>
        route?.ToLowerInvariant() switch
        {
            "pwsh" => RequestedExecutionRoute.PowerShell,
            "rtk" => RequestedExecutionRoute.Rtk,
            _ => RequestedExecutionRoute.Auto,
        };

    private static CommandAst? GetEligibleCommand(string script)
    {
        var ast = Parser.ParseInput(script, out _, out var parseErrors);
        if (parseErrors.Length > 0) return null;
        if (ast.UsingStatements.Count > 0 ||
            ast.ParamBlock is not null || ast.DynamicParamBlock is not null ||
            ast.BeginBlock is not null || ast.ProcessBlock is not null ||
            ast.CleanBlock is not null)
            return null;
        if (ast.EndBlock is null || ast.EndBlock.Statements.Count != 1) return null;
        if (ast.EndBlock.Statements[0] is not PipelineAst pipeline) return null;
        if (pipeline.Background) return null;
        if (pipeline.PipelineElements.Count != 1) return null;
        if (pipeline.PipelineElements[0] is not CommandAst command) return null;
        if (command.InvocationOperator != TokenKind.Unknown || command.Redirections.Count > 0)
            return null;

        var elements = command.CommandElements;
        if (elements.Count == 0 || elements[0] is not StringConstantExpressionAst)
            return null;

        foreach (var element in elements.Skip(1))
        {
            if (element is StringConstantExpressionAst stopParsing &&
                stopParsing.Extent.Text.Equals("--%", StringComparison.Ordinal))
            {
                return null;
            }
            var isConstant = element is ConstantExpressionAst ||
                element is CommandParameterAst parameter &&
                (parameter.Argument is null || parameter.Argument is ConstantExpressionAst);
            if (!isConstant) return null;
        }

        return command;
    }

    private static bool SupportsDirectArgumentPassing(
        string? nativeArgumentPassing,
        string? targetExecutablePath,
        ImmutableArray<string> arguments,
        bool allowUnknownModeInvariant)
    {
        if (string.Equals(
                nativeArgumentPassing,
                "Standard",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(
                nativeArgumentPassing,
                "Windows",
                StringComparison.OrdinalIgnoreCase))
        {
            return !UsesWindowsLegacyArgumentPassing(targetExecutablePath);
        }

        // The frozen cold pwsh may not be the hosted SMA version. When its
        // mode was not actually captured, route only the small argv subset
        // whose spelling is invariant between legacy and standard passing.
        // An explicit Legacy or unrecognized mode remains direct.
        return nativeArgumentPassing is null &&
               allowUnknownModeInvariant &&
               !UsesWindowsLegacyArgumentPassing(targetExecutablePath) &&
               arguments.All(IsModeInvariantArgument);
    }

    private static bool UsesWindowsLegacyArgumentPassing(string? executablePath)
    {
        var fileName = Path.GetFileName(executablePath ?? string.Empty);
        if (fileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("cscript.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("find.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("sqlcmd.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("wscript.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Path.GetExtension(fileName).ToLowerInvariant() is
            ".bat" or ".cmd" or ".js" or ".vbs" or ".wsf";
    }

    private static bool IsAlwaysExcludedLauncherExtension(string extension) =>
        extension.ToLowerInvariant() is ".bat" or ".cmd";

    internal static bool IsModeInvariantArgument(string argument)
    {
        if (argument.Length == 0) return false;
        foreach (var character in argument)
        {
            if (character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
                >= '0' and <= '9' ||
                character is '_' or '-' or '.' or '/' or ':' or '=' or '+' or
                    ',' or '%' or '@')
            {
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool TryCreateRtkArgumentVector(
        CommandAst command,
        out ImmutableArray<string> arguments)
    {
        var builder = ImmutableArray.CreateBuilder<string>(command.CommandElements.Count);
        foreach (var element in command.CommandElements)
        {
            switch (element)
            {
                case StringConstantExpressionAst text:
                    if (RequiresPowerShellArgumentExpansion(text))
                    {
                        arguments = ImmutableArray<string>.Empty;
                        return false;
                    }
                    builder.Add(text.Value);
                    break;
                case ConstantExpressionAst:
                    // Native invocation preserves the submitted spelling for
                    // numeric constants (001, 0x10, 1kb), not Value.ToString().
                    builder.Add(element.Extent.Text);
                    break;
                case CommandParameterAst { Argument: null } parameter:
                    builder.Add(parameter.Extent.Text);
                    break;
                case CommandParameterAst { Argument: ConstantExpressionAst argument }
                    parameter:
                {
                    if (argument is StringConstantExpressionAst stringValue &&
                        RequiresPowerShellArgumentExpansion(stringValue))
                    {
                        arguments = ImmutableArray<string>.Empty;
                        return false;
                    }
                    var prefixLength =
                        argument.Extent.StartOffset - parameter.Extent.StartOffset;
                    if (prefixLength < 0 || prefixLength > parameter.Extent.Text.Length)
                    {
                        arguments = ImmutableArray<string>.Empty;
                        return false;
                    }
                    var value = argument is StringConstantExpressionAst stringArgument
                        ? stringArgument.Value
                        : argument.Extent.Text;
                    builder.Add(parameter.Extent.Text[..prefixLength] + value);
                    break;
                }
                default:
                    arguments = ImmutableArray<string>.Empty;
                    return false;
            }
        }

        arguments = builder.MoveToImmutable();
        return arguments.Length > 0 && !string.IsNullOrWhiteSpace(arguments[0]);
    }

    private static bool RequiresPowerShellArgumentExpansion(
        StringConstantExpressionAst expression)
    {
        if (expression.StringConstantType != StringConstantType.BareWord)
            return false;
        var text = expression.Extent.Text;
        return text.Equals("~", StringComparison.Ordinal) ||
               text.StartsWith("~/", StringComparison.Ordinal) ||
               text.StartsWith("~\\", StringComparison.Ordinal) ||
               text.IndexOfAny(['*', '?', '[', ']']) >= 0;
    }

    private static bool IsContextChangingWrapper(CommandAst command)
    {
        if (command.CommandElements.FirstOrDefault() is not
                StringConstantExpressionAst executable)
        {
            return false;
        }

        var executableName = Path.GetFileNameWithoutExtension(executable.Value);
        return ContextChangingContainerWrappers.Contains(executableName) &&
               command.CommandElements.Skip(1)
                   .OfType<StringConstantExpressionAst>()
                   .Any(element => element.Value.Equals(
                       "exec",
                       StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksProviderQualified(string path)
    {
        // PowerShell drive letters are dynamic PSDrives, so even C:\\ is not
        // proof that Set-Content will target the filesystem provider.
        return path.Contains(':');
    }

    private static bool HasOnlyConstantArguments(CommandAst command)
    {
        foreach (var element in command.CommandElements.Skip(1))
        {
            var isConstant = element is ConstantExpressionAst ||
                element is CommandParameterAst parameter &&
                (parameter.Argument is null ||
                 parameter.Argument is ConstantExpressionAst);
            if (!isConstant) return false;
        }
        return true;
    }

    private static bool TryGetSimpleSetContentPath(
        CommandAst sink,
        out StringConstantExpressionAst path)
    {
        path = null!;
        var elements = sink.CommandElements;
        if (elements.Count == 2 &&
            elements[1] is StringConstantExpressionAst positional)
        {
            path = positional;
            return true;
        }
        if (elements.Count == 2 && elements[1] is CommandParameterAst
            {
                ParameterName: var parameterName,
                Argument: StringConstantExpressionAst attached,
            } && parameterName.Equals("Path", StringComparison.OrdinalIgnoreCase))
        {
            path = attached;
            return true;
        }
        if (elements.Count == 3 && elements[1] is CommandParameterAst
            {
                ParameterName: var separatedName,
                Argument: null,
            } && separatedName.Equals("Path", StringComparison.OrdinalIgnoreCase) &&
            elements[2] is StringConstantExpressionAst separated)
        {
            path = separated;
            return true;
        }
        return false;
    }

    private static ExecutionDomain? ClassifyDomain(
        string script,
        TrustedCommandSnapshot commands)
    {
        var ast = Parser.ParseInput(script, out _, out var parseErrors);
        if (parseErrors.Length > 0) return null;
        if (ast.UsingStatements.Count > 0 ||
            ast.ParamBlock is not null || ast.DynamicParamBlock is not null ||
            ast.BeginBlock is not null || ast.ProcessBlock is not null ||
            ast.CleanBlock is not null)
            return ExecutionDomain.MixedDataflow;
        if (ast.EndBlock is null || ast.EndBlock.Statements.Count == 0)
            return ExecutionDomain.PowerShell;
        if (ast.EndBlock.Statements.Count != 1)
            return ExecutionDomain.MixedDataflow;
        if (ast.EndBlock.Statements[0] is not PipelineAst pipeline)
            return ExecutionDomain.MixedDataflow;
        if (pipeline.Background)
            return ExecutionDomain.MixedDataflow;
        if (pipeline.PipelineElements.Count != 1)
            return ExecutionDomain.MixedDataflow;
        if (pipeline.PipelineElements[0] is not CommandAst command)
            return ExecutionDomain.PowerShell;
        if (command.Redirections.Count > 0)
            return ExecutionDomain.MixedDataflow;

        var first = command.CommandElements.FirstOrDefault();
        if (first is not StringConstantExpressionAst commandName)
            return null;
        return commands.Resolve(commandName.Value, CommandTypes.All)?.CommandType switch
        {
            CommandTypes.Application => ExecutionDomain.NativeTerminal,
            CommandTypes.Alias or CommandTypes.Function or CommandTypes.Cmdlet or
                CommandTypes.ExternalScript or CommandTypes.Filter or CommandTypes.Configuration =>
                ExecutionDomain.PowerShell,
            _ => null,
        };
    }
}
