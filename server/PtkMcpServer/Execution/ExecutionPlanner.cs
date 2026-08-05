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
        string? rewrittenScript = null)
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

        // RTK owns the routing decision; the caller obtained its answer from
        // RtkCommandRewriter and passes it here as data, so this planner stays
        // pure — it never starts a process or resolves an executable on disk.
        // The rewrite is a shell command line PowerShell 7 executes natively,
        // so compound and env-prefixed commands route without PTK modelling
        // any of that.
        string? rewritten = null;
        if (!string.IsNullOrWhiteSpace(rewrittenScript) &&
            !string.Equals(rewrittenScript, script, StringComparison.Ordinal))
        {
            TryBindRewrite(
                script,
                rewrittenScript,
                commands,
                effectiveRtkIdentity,
                out rewritten);
        }
        if (rewritten is null)
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

        // The rewrite executes in the warm runspace like any other script, so
        // the session keeps its variables, modules, and location. Output is
        // RTK-produced and must not be sent through `rtk log` a second time.
        return new ExecutionPlan(
            script,
            executionScript: rewritten,
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
            directFallbackProvenance:
                resolutionContext == ResolutionContext.Cold || !compressAvailable
                    ? OutputProvenance.DirectText
                    : OutputProvenance.PowerShellObjects);
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

    /// <summary>
    /// RTK rewrites command <em>text</em> and holds no session state, so it
    /// returns <c>rtk git status</c> even when the session defines
    /// <c>function git</c>. Executing that rewrite would run a different
    /// command than the one submitted. Slice 0 keeps inherited user state out
    /// of the session, but a script can still define a name mid-session, so
    /// every name RTK wrapped must still resolve to a native application here.
    ///
    /// Declines the whole rewrite rather than any segment of it: a partially
    /// applied rewrite is a third execution shape nobody asked for.
    /// </summary>
    private static bool TryBindRewrite(
        string script,
        string rewritten,
        TrustedCommandSnapshot commands,
        RtkExecutableIdentity rtk,
        out string? boundScript)
    {
        boundScript = null;
        var ast = Parser.ParseInput(rewritten, out _, out var parseErrors);
        if (parseErrors.Length > 0)
            return false;

        var wrappedCount = 0;
        var stripped = new System.Text.StringBuilder(rewritten);
        var bound = new System.Text.StringBuilder(rewritten);

        // Names the submitted script itself defines. The command snapshot is
        // captured in preflight, before the worker executes anything, so a
        // function defined by this very script does not exist yet and its name
        // still resolves to the native application (GitHub #37). The submitted
        // AST does show the definition, so it is the authority here.
        var bindings = DefinedCommandNames(script);
        if (bindings.BindsUnknownNames)
        {
            // The script can bind command names this reader cannot enumerate —
            // dot-sourcing, Import-Module, Import-Alias, Invoke-Expression.
            // Any wrapped name might be one of them, so route nothing.
            return false;
        }

        // Walk in reverse so removing a head token cannot move an earlier one.
        var commandAsts = ast
            .FindAll(node => node is CommandAst, searchNestedScriptBlocks: true)
            .Cast<CommandAst>()
            .OrderByDescending(command => command.Extent.StartOffset)
            .ToList();

        foreach (var command in commandAsts)
        {
            if (command.CommandElements.FirstOrDefault() is not
                StringConstantExpressionAst head)
            {
                // A command name PTK cannot read statically is not one it can
                // prove safe to route.
                return false;
            }
            if (!Path.GetFileNameWithoutExtension(head.Value)
                    .Equals("rtk", StringComparison.OrdinalIgnoreCase))
            {
                // A segment RTK left alone; it executes exactly as submitted.
                continue;
            }

            // `rtk <name> ...` — the wrapped name is the next element, and it
            // must be a native application. RTK holds no session state, so it
            // would happily wrap a name the session bound to a function.
            if (command.CommandElements.Count < 2 ||
                command.CommandElements[1] is not StringConstantExpressionAst wrapped)
            {
                return false;
            }
            if (bindings.Names.Contains(BareCommandName(wrapped.Value)))
            {
                // The caller's own definition wins. Routing here would run the
                // native binary instead of the command they submitted.
                return false;
            }
            if (commands.Resolve(wrapped.Value, CommandTypes.All)?.CommandType !=
                CommandTypes.Application)
            {
                return false;
            }

            wrappedCount++;

            // Remove this `rtk` head plus the whitespace after it.
            var start = head.Extent.StartOffset;
            var length = command.CommandElements[1].Extent.StartOffset - start;
            stripped.Remove(start, length);

            // Bind the bare name RTK emitted to the exact executable PTK
            // pinned at startup. Left as `rtk`, the rewrite would resolve
            // through PATH when the script runs and could execute a different
            // binary than the one whose identity PTK verified.
            bound.Remove(start, head.Extent.EndOffset - start);
            bound.Insert(start, "& '" + rtk.ExecutablePath.Replace("'", "''") + "'");
        }

        // A rewrite that routes nothing through RTK buys nothing, and is the
        // shape a non-RTK binary on PTK_RTK_PATH produces when it merely echoes
        // its arguments.
        if (wrappedCount == 0)
            return false;

        // The decisive check: RTK only ever *inserts* `rtk ` before segments it
        // recognizes. Removing those prefixes must therefore reproduce exactly
        // what was submitted. Anything else — reordered, edited, or invented
        // text — is not a rewrite PTK will execute in the caller's name.
        // Exact, not whitespace-normalized. Normalizing collapsed runs of
        // whitespace anywhere — including inside a quoted argument — so a
        // rewrite that changed `git commit -m "a  b"` to `... "a b"` reduced
        // to the same string and was accepted, then executed with different
        // argument text. RTK inserts `rtk ` and a single following space and
        // changes nothing else, so removing exactly that must reproduce the
        // submitted text byte for byte. Only leading and trailing whitespace
        // is forgiven, because the submitted text is trimmed before use.
        if (!string.Equals(
                stripped.ToString().Trim(),
                script.Trim(),
                StringComparison.Ordinal))
        {
            return false;
        }

        boundScript = bound.ToString();
        return true;
    }


    /// <summary>
    /// Cmdlets that bind an alias whose name appears in the call itself, so a
    /// static read can name it.
    /// </summary>
    private static readonly HashSet<string> NamedAliasCommands = new(
        ["Set-Alias", "sal", "New-Alias", "nal"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Commands that can bind command names this reader cannot enumerate:
    /// the names live in a file or module, not in the submitted text. A script
    /// using one of these could shadow anything, so nothing in it is routed.
    /// </summary>
    private static readonly HashSet<string> OpaqueBindingCommands = new(
        [
            "Import-Alias", "ipal",
            "Import-Module", "ipmo",
            "Invoke-Expression", "iex",
            "New-Module", "nmo",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>What the submitted script binds for itself.</summary>
    /// <param name="Names">Names it binds that can be read statically.</param>
    /// <param name="BindsUnknownNames">
    /// True when the script can bind names this reader cannot enumerate —
    /// dot-sourcing, module import, alias import, <c>Invoke-Expression</c>.
    /// The caller must decline every rewrite, since any wrapped name might be
    /// one of them.
    /// </param>
    private readonly record struct ScriptBindings(
        HashSet<string> Names,
        bool BindsUnknownNames);

    /// <summary>
    /// Strips what PowerShell strips when it exposes a command name. A
    /// declaration reads <c>function global:git</c> but the callable name is
    /// <c>git</c>; a call reads <c>Microsoft.PowerShell.Utility\Set-Alias</c>
    /// but the cmdlet is <c>Set-Alias</c>. Comparing raw text misses both, so
    /// both are normalized to the bare name before comparison.
    /// </summary>
    private static string BareCommandName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        // Module qualification: Module\Command, or a path-qualified script.
        var separator = name.LastIndexOfAny(['\\', '/']);
        if (separator >= 0 && separator < name.Length - 1)
            name = name[(separator + 1)..];

        // Scope qualification on a declaration: global:, script:, local:,
        // private:, using:. Only a known scope word is stripped — a colon can
        // legitimately appear in a function name otherwise.
        var colon = name.IndexOf(':');
        if (colon > 0)
        {
            var scope = name[..colon];
            if (scope.Equals("global", StringComparison.OrdinalIgnoreCase) ||
                scope.Equals("script", StringComparison.OrdinalIgnoreCase) ||
                scope.Equals("local", StringComparison.OrdinalIgnoreCase) ||
                scope.Equals("private", StringComparison.OrdinalIgnoreCase) ||
                scope.Equals("using", StringComparison.OrdinalIgnoreCase))
            {
                name = name[(colon + 1)..];
            }
        }

        return name;
    }

    /// <summary>
    /// Every command name the submitted script binds for itself: `function`
    /// and `filter` declarations at any nesting depth, and alias creation whose
    /// name is a readable literal — each normalized to the bare name PowerShell
    /// will actually expose. Where the script can bind names that are not
    /// readable here at all, it reports that instead, and the caller declines
    /// wholesale.
    /// </summary>
    private static ScriptBindings DefinedCommandNames(string script)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ast = Parser.ParseInput(script, out _, out var parseErrors);
        if (parseErrors.Length > 0)
        {
            // Unparseable submitted text is not routed anyway (the reduction
            // check fails first); returning empty keeps this pure.
            return new ScriptBindings(names, BindsUnknownNames: false);
        }

        foreach (var definition in ast
                     .FindAll(node => node is FunctionDefinitionAst, searchNestedScriptBlocks: true)
                     .Cast<FunctionDefinitionAst>())
        {
            if (!string.IsNullOrEmpty(definition.Name))
                names.Add(BareCommandName(definition.Name));
        }

        // Dot-sourcing runs another file's definitions into this scope; the
        // names it binds are not in this text.
        var bindsUnknown = ast
            .FindAll(node => node is CommandAst, searchNestedScriptBlocks: true)
            .Cast<CommandAst>()
            .Any(command => command.InvocationOperator == TokenKind.Dot);

        foreach (var command in ast
                     .FindAll(node => node is CommandAst, searchNestedScriptBlocks: true)
                     .Cast<CommandAst>())
        {
            if (command.CommandElements.FirstOrDefault() is not
                StringConstantExpressionAst head)
            {
                continue;
            }

            var bare = BareCommandName(head.Value);
            if (OpaqueBindingCommands.Contains(bare))
            {
                bindsUnknown = true;
                continue;
            }
            if (!NamedAliasCommands.Contains(bare))
                continue;

            if (TryReadAliasName(command, out var aliasName))
                names.Add(BareCommandName(aliasName));
            else
                bindsUnknown = true;
        }

        return new ScriptBindings(names, bindsUnknown);
    }

    /// <summary>
    /// The alias name from a Set-Alias/New-Alias call: an explicit
    /// <c>-Name</c>, else the first positional argument. Named parameters
    /// consume their separated argument, so the value of an unrelated
    /// parameter is never mistaken for the positional name. Returns false when
    /// the name is not a readable literal, which the caller treats as an
    /// unknown binding rather than as no binding.
    /// </summary>
    private static bool TryReadAliasName(CommandAst command, out string aliasName)
    {
        aliasName = string.Empty;
        var elements = command.CommandElements;
        StringConstantExpressionAst? firstPositional = null;
        var sawUnreadableParameterName = false;

        for (var index = 1; index < elements.Count; index++)
        {
            if (elements[index] is CommandParameterAst parameter)
            {
                var isName = "Name".StartsWith(
                    parameter.ParameterName,
                    StringComparison.OrdinalIgnoreCase);

                // -Name:value binds inline; -Name value takes the next element.
                CommandElementAst? argument = parameter.Argument;
                if (argument is null && index + 1 < elements.Count &&
                    elements[index + 1] is not CommandParameterAst)
                {
                    argument = elements[index + 1];
                    index++; // consumed, so it is not read as positional
                }

                if (!isName) continue;
                if (argument is StringConstantExpressionAst named)
                {
                    aliasName = named.Value;
                    return true;
                }
                // An explicit -Name whose value is an expression.
                sawUnreadableParameterName = true;
                continue;
            }

            firstPositional ??= elements[index] as StringConstantExpressionAst;
            if (firstPositional is null && elements[index] is not CommandParameterAst)
            {
                // A positional argument that is not a literal could be the name.
                sawUnreadableParameterName = true;
            }
        }

        if (sawUnreadableParameterName) return false;
        if (firstPositional is null) return false;
        aliasName = firstPositional.Value;
        return true;
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
