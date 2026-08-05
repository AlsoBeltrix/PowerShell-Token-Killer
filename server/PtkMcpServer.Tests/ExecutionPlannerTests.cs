using System.Collections.Immutable;
using System.Management.Automation;

namespace PtkMcpServer.Tests;

public sealed class ExecutionPlannerTests
{
    private static readonly string RtkPath =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trusted", "rtk"));


    /// <summary>
    /// Slice 2: RTK returns a bare `rtk` head. Executing that text verbatim
    /// would resolve `rtk` through PATH at run time and could run a different
    /// binary than the one PTK pinned and hashed at startup. The plan must
    /// bind the pinned absolute path instead.
    /// </summary>
    [Fact]
    public void Accepted_rewrite_binds_the_startup_pinned_executable_not_a_path_lookup()
    {
        var plan = Plan(
            "git status",
            "auto",
            RtkPath,
            Application("git", "/usr/bin/git"),
            rewrittenScript: "rtk git status");

        Assert.Equal(ExecutionPath.Rtk, plan.ExecutionPath);
        Assert.NotNull(plan.ExecutionScript);
        Assert.Contains(RtkPath, plan.ExecutionScript);
        Assert.DoesNotMatch(@"^\s*rtk\s", plan.ExecutionScript);
        Assert.EndsWith("git status", plan.ExecutionScript);
    }

    /// <summary>
    /// A redirection does not make a rewrite ineligible. RTK rewrites
    /// `git status --short 2>&amp;1` (verified against rtk 0.44.2), the
    /// redirection binds to the `rtk` head, and stripping the prefix
    /// reproduces the submitted text — so the plan must route it rather
    /// than falling back and losing the compression.
    /// </summary>
    [Theory]
    [InlineData("git status --short", "rtk git status --short")]
    [InlineData("git status --short 2>&1", "rtk git status --short 2>&1")]
    public void A_redirection_does_not_block_an_otherwise_valid_rewrite(
        string script,
        string rewritten)
    {
        var commands = Application("git", "/usr/bin/git");
        var plan = Plan(script, "rtk", RtkPath, commands, rewrittenScript: rewritten);

        Assert.Equal(ExecutionPath.Rtk, plan.ExecutionPath);
        Assert.Null(plan.FallbackReason);
    }

    /// <summary>
    /// GitHub #37: the command snapshot is captured in preflight, before the
    /// worker runs anything, so a function the submitted script defines does
    /// not exist yet and its name still resolves to the native application.
    /// The rewrite was accepted and the real binary ran instead of the
    /// caller's function — a different command than the one submitted.
    ///
    /// The submitted script's own AST does show the definition, so a name
    /// defined anywhere in the script must never be routed, whatever the
    /// snapshot says.
    /// </summary>
    [Theory]
    [InlineData("function git { 'MINE' }; git status", "function git { 'MINE' }; rtk git status")]
    [InlineData("filter git { 'MINE' }; git status", "filter git { 'MINE' }; rtk git status")]
    [InlineData("workflow git { 'MINE' }; git status", "workflow git { 'MINE' }; rtk git status")]
    [InlineData("Set-Alias git Get-Date; git status", "Set-Alias git Get-Date; rtk git status")]
    [InlineData("New-Alias git Get-Date; git status", "New-Alias git Get-Date; rtk git status")]
    public void A_name_the_submitted_script_defines_is_never_routed(
        string script,
        string rewritten)
    {
        var plan = Plan(
            script,
            "auto",
            RtkPath,
            Application("git", "/usr/bin/git"),
            rewrittenScript: rewritten);

        AssertDirect(plan, script, RequestedExecutionRoute.Auto);
    }

    /// <summary>
    /// The #37 guard must not over-reach: a script that merely mentions a
    /// command name, or defines some unrelated function, still routes.
    /// </summary>
    [Theory]
    [InlineData("git status", "rtk git status")]
    [InlineData("function helper { 'x' }; git status", "function helper { 'x' }; rtk git status")]
    public void A_script_defining_an_unrelated_name_still_routes(
        string script,
        string rewritten)
    {
        var plan = Plan(
            script,
            "auto",
            RtkPath,
            Application("git", "/usr/bin/git"),
            rewrittenScript: rewritten);

        Assert.Equal(ExecutionPath.Rtk, plan.ExecutionPath);
    }

    /// <summary>
    /// Slice 2: RTK only ever inserts `rtk ` before segments it recognizes, so
    /// stripping those prefixes must reproduce the submitted text exactly. A
    /// binary on PTK_RTK_PATH that is not RTK — one that merely echoes its
    /// arguments, or edits the command — must not have its answer executed in
    /// the caller's name.
    /// </summary>
    [Theory]
    [InlineData("hook check --agent ptk git status")]   // argument echo
    [InlineData("rtk git push")]                        // edited command
    [InlineData("rtk git status && rm -rf /")]          // appended command
    [InlineData("git status")]                          // nothing wrapped
    public void Rewrite_that_does_not_reduce_to_the_submitted_text_is_declined(
        string rewritten)
    {
        var plan = Plan(
            "git status",
            "auto",
            RtkPath,
            Application("git", "/usr/bin/git"),
            rewrittenScript: rewritten);

        AssertDirect(plan, "git status", RequestedExecutionRoute.Auto);
    }

    /// <summary>
    /// The strip-and-compare must be exact, not whitespace-normalized. A
    /// normalized comparison collapsed runs of whitespace anywhere — including
    /// inside a quoted argument — so a rewrite that silently changed the text
    /// of an argument reduced to the same string and was accepted, then
    /// executed with different argument content than the caller submitted.
    /// </summary>
    [Fact]
    public void Rewrite_altering_whitespace_inside_a_quoted_argument_is_declined()
    {
        const string script = "git commit -m \"two  spaces\"";

        var plan = Plan(
            script,
            "auto",
            RtkPath,
            Application("git", "/usr/bin/git"),
            rewrittenScript: "rtk git commit -m \"two spaces\"");

        AssertDirect(plan, script, RequestedExecutionRoute.Auto);
    }

    /// <summary>
    /// Slice 2: RTK holds no session state, so it wraps a name the session may
    /// have bound to something other than a native executable. Executing that
    /// rewrite would run a different command than the caller submitted.
    /// </summary>
    [Theory]
    [InlineData(CommandTypes.Function)]
    [InlineData(CommandTypes.Alias)]
    [InlineData(CommandTypes.Cmdlet)]
    [InlineData(CommandTypes.ExternalScript)]
    public void Rewrite_wrapping_a_non_native_binding_is_declined(CommandTypes type)
    {
        var commands = new TrustedCommandSnapshot();
        commands.Set("git", CommandTypes.All, new ResolvedCommand(type));

        var plan = Plan(
            "git status",
            "auto",
            RtkPath,
            commands,
            rewrittenScript: "rtk git status");

        AssertDirect(plan, "git status", RequestedExecutionRoute.Auto);
    }

    [Fact]
    public void Moduleless_warm_rtk_plan_freezes_a_direct_text_fallback()
    {
        var plan = ExecutionPlanner.Create(
            "git status",
            "auto",
            new RtkExecutableIdentity(RtkPath),
            Application("git", "/usr/bin/git"),
            compressAvailable: false,
            ResolutionContext.Warm,
            workingDirectory: Path.GetFullPath(Path.GetTempPath()),
            rewrittenScript: "rtk git status");

        Assert.Equal(ExecutionPath.Rtk, plan.ExecutionPath);
        Assert.Equal(OutputProvenance.DirectText, plan.DirectFallbackProvenance);
        var fallback = ExecutionDispatch.RtkUnavailableFallback(plan);
        Assert.Equal(ExecutionPath.PowerShellDirect, fallback.ExecutionPath);
        Assert.Equal(OutputProvenance.DirectText, fallback.OutputProvenance);
    }



    [Theory]
    [InlineData("")]
    [InlineData("param(); git status")]
    [InlineData("begin { git status }")]
    [InlineData("process { git status }")]
    [InlineData("git status; git diff")]
    [InlineData("if ($true) { git status }")]
    [InlineData("git status | Out-Null")]
    [InlineData("1 + 2")]
    [InlineData("& git status")]
    [InlineData("git log -1 > out.txt")]
    [InlineData("$cmd status")]
    [InlineData("rtk gain")]
    [InlineData("/opt/RTK.EXE gain")]
    [InlineData("git commit -m \"$msg\"")]
    [InlineData("git -flag:$value")]
    [InlineData("git --% -x \"a b\"")]
    [InlineData("git ~/repo")]
    [InlineData("git -C:~/repo status")]
    [InlineData("git *.md")]
    [InlineData("git file?.md")]
    [InlineData("git status ||| (")]
    public void Keeps_every_non_single_constant_command_shape_on_PowerShell(string script)
    {
        var plan = Plan(script, "auto", RtkPath, Application("git", "/usr/bin/git"));

        AssertDirect(plan, script, RequestedExecutionRoute.Auto);
    }











    [Theory]
    [InlineData("clean { 'cleanup' } end { git status }")]
    [InlineData("dynamicparam { } end { git status }")]
    public void Keeps_clean_and_dynamicparam_blocks_on_the_exact_PowerShell_path(string script)
    {
        var plan = Plan(script, "auto", RtkPath, Application("git", "/usr/bin/git"));

        AssertDirect(plan, script, RequestedExecutionRoute.Auto);
        Assert.Equal(ExecutionDomain.MixedDataflow, plan.Domain);
    }

    [Fact]
    public void Keeps_top_level_using_statements_on_the_exact_PowerShell_path()
    {
        const string script = "using module './Example.psm1'; git status";

        var plan = Plan(script, "auto", RtkPath, Application("git", "/usr/bin/git"));

        AssertDirect(plan, script, RequestedExecutionRoute.Auto);
        Assert.Equal(ExecutionDomain.MixedDataflow, plan.Domain);
    }

    [Fact]
    public void Keeps_a_background_native_pipeline_on_the_exact_PowerShell_path()
    {
        const string script = "git status &";
        var commands = Application("git", "/usr/bin/git");

        var automatic = Plan(script, "auto", RtkPath, commands);
        var forced = Plan(script, "rtk", RtkPath, commands);

        AssertDirect(automatic, script, RequestedExecutionRoute.Auto);
        Assert.Equal(ExecutionDomain.MixedDataflow, automatic.Domain);
        AssertDirect(forced, script, RequestedExecutionRoute.Rtk);
        Assert.Equal(ExecutionDomain.MixedDataflow, forced.Domain);
        Assert.Equal(ExecutionFallbackReason.RtkIneligibleShape, forced.FallbackReason);
    }


    [Theory]
    [InlineData(CommandTypes.Alias, null, null)]
    [InlineData(CommandTypes.Function, null, null)]
    [InlineData(CommandTypes.Cmdlet, null, null)]
    [InlineData(CommandTypes.ExternalScript, "/tmp/git.ps1", null)]
    [InlineData(CommandTypes.Application, "/tmp/git.cmd", null)]
    [InlineData(CommandTypes.Application, "/tmp/git.BAT", null)]
    public void Auto_route_keeps_non_native_or_batch_resolution_on_PowerShell(
        CommandTypes type,
        string? source,
        string? definition)
    {
        var commands = new TrustedCommandSnapshot();
        commands.Set("git", CommandTypes.All, new ResolvedCommand(type, source, definition));

        var plan = Plan("git status", "auto", RtkPath, commands);

        AssertDirect(plan, "git status", RequestedExecutionRoute.Auto);
        Assert.Equal(
            type == CommandTypes.Application
                ? ExecutionDomain.NativeTerminal
                : ExecutionDomain.PowerShell,
            plan.Domain);
    }

    [Fact]
    public void Honors_absent_rtk_pwsh_and_strict_forced_rtk_contracts()
    {
        var commands = new TrustedCommandSnapshot();
        commands.Set("Get-ChildItem", CommandTypes.All, new ResolvedCommand(CommandTypes.Cmdlet));

        var absent = Plan("git status", "auto", null, commands);
        AssertDirect(absent, "git status", RequestedExecutionRoute.Auto);
        Assert.Null(absent.Domain);
        Assert.Null(absent.OutputShapingRtkIdentity);

        var empty = Plan("git status", "auto", string.Empty, commands);
        AssertDirect(empty, "git status", RequestedExecutionRoute.Auto);
        Assert.Null(empty.Domain);
        Assert.Null(empty.OutputShapingRtkIdentity);

        var pwsh = Plan("git status", "PWSH", RtkPath, commands);
        AssertDirect(pwsh, "git status", RequestedExecutionRoute.PowerShell);
        Assert.Null(pwsh.Domain);
        Assert.Equal(RtkPath, pwsh.OutputShapingRtkIdentity?.ExecutablePath);

        // route=rtk asserts the route but does not override RTK's judgment:
        // when RTK declines to rewrite, the exact original still executes as
        // PowerShell with a labeled fallback.
        var forcedDecline = Plan("Get-ChildItem", "RTK", RtkPath, commands);
        AssertDirect(forcedDecline, "Get-ChildItem", RequestedExecutionRoute.Rtk);
        Assert.Equal(RtkPath, forcedDecline.OutputShapingRtkIdentity?.ExecutablePath);
        Assert.Equal(ExecutionDomain.PowerShell, forcedDecline.Domain);
        Assert.Equal(
            ExecutionFallbackReason.RtkIneligibleShape,
            forcedDecline.FallbackReason);

        var forcedFallback = Plan("git status | Out-Null", "rtk", RtkPath, commands);
        AssertDirect(
            forcedFallback,
            "git status | Out-Null",
            RequestedExecutionRoute.Rtk);
        Assert.Equal(ExecutionDomain.MixedDataflow, forcedFallback.Domain);
        Assert.Equal(ExecutionFallbackReason.RtkIneligibleShape, forcedFallback.FallbackReason);
    }

    [Fact]
    public void Planner_has_no_legacy_raw_policy_input()
    {
        var create = typeof(ExecutionPlanner).GetMethod(
            nameof(ExecutionPlanner.Create),
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic)!;

        Assert.DoesNotContain(
            create.GetParameters(),
            parameter => parameter.Name == "raw");

        var plan = Plan(
            "git status",
            "rtk",
            RtkPath,
            Application("git", "/usr/bin/git"),
            rewrittenScript: "rtk git status");

        Assert.Equal(ExecutionPath.Rtk, plan.ExecutionPath);
        Assert.Equal(OutputProvenance.RtkUnknown, plan.OutputProvenance);
        Assert.Equal(RequestedExecutionRoute.Rtk, plan.RequestedRoute);
        // The bare `rtk` head binds to the startup-pinned executable so PATH
        // cannot substitute a different binary at execution time.
        Assert.Contains(RtkPath, plan.ExecutionScript);
        Assert.EndsWith("git status", plan.ExecutionScript);
        Assert.Collection(
            plan.PermittedFallbacks,
            fallback => Assert.Equal(ExecutionPath.PowerShellDirect, fallback));
        Assert.Null(plan.FallbackReason);
    }

    [Fact]
    public void Compressor_unavailable_direct_plan_has_unknown_domain_and_direct_text()
    {
        var plan = ExecutionPlanner.CreateDirect(
            "'plain text'",
            "auto",
            compressAvailable: false,
            ResolutionContext.Warm);

        Assert.Null(plan.Domain);
        Assert.Equal(ExecutionPath.PowerShellDirect, plan.ExecutionPath);
        Assert.Equal(OutputProvenance.DirectText, plan.OutputProvenance);
        Assert.Empty(plan.PermittedFallbacks);
    }

    [Fact]
    public void Machine_codes_cover_every_frozen_plan_value()
    {
        Assert.Equal(
            ["powershell", "native_terminal", "mixed_dataflow"],
            Enum.GetValues<ExecutionDomain>().Select(value => value.ToMachineCode()));
        Assert.Equal(
            ["powershell_direct", "rtk", "native_direct"],
            Enum.GetValues<ExecutionPath>().Select(value => value.ToMachineCode()));
        Assert.Equal(
            ["auto", "pwsh", "rtk"],
            Enum.GetValues<RequestedExecutionRoute>().Select(value => value.ToMachineCode()));
        Assert.Equal(
            ["powershell_objects", "direct_text", "rtk_unknown", "rtk_filtered", "rtk_passthrough"],
            Enum.GetValues<OutputProvenance>().Select(value => value.ToMachineCode()));
        Assert.Equal(
            [
                "rtk_executable_unavailable",
                "rtk_executable_became_unavailable",
                "rtk_ineligible_shape",
                "rtk_self_invocation",
                "rtk_resolution_not_application",
                "rtk_fidelity_exclusion",
                "rtk_execution_preparation_failed",
                "rtk_target_resolution_changed",
            ],
            Enum.GetValues<ExecutionFallbackReason>().Select(value => value.ToMachineCode()));
    }

    /// <summary>
    /// Slice 1 regression: a mixed native/PowerShell dataflow plan still
    /// classifies and executes as before, and carries no suggestion surface.
    /// </summary>
    [Fact]
    public void Mixed_dataflow_plans_execute_unchanged_and_carry_no_suggestion()
    {
        var commands = Application("git", "/usr/bin/git");
        commands.Set(
            "Set-Content",
            CommandTypes.All,
            new ResolvedCommand(
                CommandTypes.Cmdlet,
                Source: "Microsoft.PowerShell.Management",
                IsCanonicalManagementSetContent: true));

        const string original = "git diff | Set-Content -Path 'patch file.txt'";
        var plan = Plan(original, "auto", RtkPath, commands);

        Assert.Equal(ExecutionDomain.MixedDataflow, plan.Domain);
        Assert.Equal(ExecutionPath.PowerShellDirect, plan.ExecutionPath);
        Assert.Equal(original, plan.ExecutionScript);
    }
    [Fact]
    public void Plan_constructor_rejects_false_rtk_identity_or_provenance()
    {
        var identity = new RtkExecutableIdentity(RtkPath);

        Assert.Throws<ArgumentException>(() => new ExecutionPlan(
            "git status",
            "git status",
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.DirectText,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null,
            identity));
        Assert.Throws<ArgumentException>(() => new ExecutionPlan(
            "'direct'",
            "'direct'",
            ExecutionDomain.PowerShell,
            ExecutionPath.PowerShellDirect,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.PowerShellObjects,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null,
            identity));
        Assert.Throws<ArgumentException>(() => new ExecutionPlan(
            "'direct'",
            "'direct'",
            ExecutionDomain.PowerShell,
            ExecutionPath.PowerShellDirect,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null,
            rtkExecutableIdentity: null));
        Assert.Throws<ArgumentException>(() => new ExecutionPlan(
            "'direct'",
            "'direct'",
            ExecutionDomain.PowerShell,
            ExecutionPath.PowerShellDirect,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.PowerShellObjects,
            ImmutableArray<ExecutionPath>.Empty,
            ExecutionFallbackReason.RtkIneligibleShape,
            rtkExecutableIdentity: null));
    }



    [Theory]
    [InlineData("git status | Out-Null", "mixed_dataflow")]
    [InlineData("git log -1 > out.txt", "mixed_dataflow")]
    [InlineData("1 + 2", "powershell")]
    public void Classifies_domain_independently_from_the_direct_execution_path(
        string script,
        string expectedDomain)
    {
        var plan = Plan(script, "auto", RtkPath, Application("git", "/usr/bin/git"));

        Assert.Equal(ExecutionPath.PowerShellDirect, plan.ExecutionPath);
        Assert.Equal(expectedDomain, plan.Domain?.ToMachineCode());
    }

    /// <summary>
    /// Models the caller: RTK is asked for a rewrite and its answer is handed
    /// to the planner as data. A null <paramref name="rewrittenScript"/> is a
    /// decline, which must keep the exact original on PowerShell.
    /// </summary>
    private static ExecutionPlan Plan(
        string script,
        string route,
        string? rtkPath,
        TrustedCommandSnapshot commands,
        string? rewrittenScript = null) =>
        ExecutionPlanner.Create(
            script,
            route,
            rtkPath is null ? null : new RtkExecutableIdentity(rtkPath),
            commands,
            compressAvailable: true,
            ResolutionContext.Warm,
            workingDirectory: Path.GetFullPath(Path.GetTempPath()),
            rewrittenScript: rewrittenScript);

    private static void AssertDirect(
        ExecutionPlan plan,
        string script,
        RequestedExecutionRoute requestedRoute,
        OutputProvenance expectedProvenance = OutputProvenance.PowerShellObjects)
    {
        Assert.Equal(script, plan.OriginalScript);
        Assert.Equal(script, plan.ExecutionScript);
        Assert.Equal(ExecutionPath.PowerShellDirect, plan.ExecutionPath);
        Assert.Equal("powershell_direct", plan.EffectiveRoute);
        Assert.Equal(PreExecutionValidation.None, plan.PreExecutionValidation);
        Assert.Equal(ResolutionContext.Warm, plan.ResolutionContext);
        Assert.Equal(requestedRoute, plan.RequestedRoute);
        Assert.Equal(expectedProvenance, plan.OutputProvenance);
        Assert.Empty(plan.PermittedFallbacks);
        Assert.Null(plan.RtkExecutableIdentity);
    }

    private static TrustedCommandSnapshot Application(string name, string source)
    {
        var commands = new TrustedCommandSnapshot();
        commands.Set(name, CommandTypes.All,
            new ResolvedCommand(CommandTypes.Application, source));
        return commands;
    }
}
