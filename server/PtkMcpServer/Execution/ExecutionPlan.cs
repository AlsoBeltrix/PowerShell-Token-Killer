using System.Collections.Immutable;

namespace PtkMcpServer;

internal enum ExecutionDomain
{
    PowerShell,
    NativeTerminal,
    MixedDataflow,
}

internal enum ExecutionPath
{
    PowerShellDirect,
    Rtk,
    NativeDirect,
}

internal enum PreExecutionValidation
{
    None,
}

internal enum ResolutionContext
{
    Warm,
    Cold,
}

internal enum RequestedExecutionRoute
{
    Auto,
    PowerShell,
    Rtk,
}

internal enum ExecutionFallbackReason
{
    RtkExecutableUnavailable,
    RtkExecutableBecameUnavailable,
    RtkIneligibleShape,
    RtkSelfInvocation,
    RtkResolutionNotApplication,
    RtkFidelityExclusion,
    RtkExecutionPreparationFailed,
    RtkTargetResolutionChanged,
}

internal enum OutputProvenance
{
    PowerShellObjects,
    DirectText,
    RtkUnknown,
    RtkFiltered,
    RtkPassthrough,
}

internal sealed record RtkVerifiedBinaryIdentity
{
    internal RtkVerifiedBinaryIdentity(string version, string binaryDigest)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(binaryDigest);
        if (string.IsNullOrWhiteSpace(version) || version.Length > 128)
            throw new ArgumentException("RTK version must be 1..128 characters.", nameof(version));
        if (binaryDigest.Length != 64 ||
            binaryDigest.Any(character => character is not (
                >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "RTK binary digest must be lowercase SHA-256 hex.",
                nameof(binaryDigest));
        }

        Version = version;
        BinaryDigest = binaryDigest;
    }

    internal string Version { get; }
    internal string BinaryDigest { get; }
}

internal sealed record RtkExecutableIdentity(
    string ExecutablePath,
    RtkVerifiedBinaryIdentity? Verified = null,
    string? CapturedBinaryDigest = null,
    UnixFileMode? CapturedUnixFileMode = null)
{
    internal string? AuditBinaryDigest =>
        Verified?.BinaryDigest ?? CapturedBinaryDigest;

    internal static RtkExecutableIdentity? TryCapture(
        string? path,
        RtkVerifiedBinaryIdentity? verified = null)
    {
        var identity = ExecutableFileIdentity.TryCapture(path);
        return identity is null
            ? null
            : new RtkExecutableIdentity(
                identity.ExecutablePath,
                verified,
                identity.BinaryDigest,
                identity.UnixFileMode);
    }

    internal bool MatchesCurrentFile()
    {
        if (CapturedBinaryDigest is null)
            return File.Exists(ExecutablePath);
        var current = ExecutableFileIdentity.TryCapture(ExecutablePath);
        return current is not null &&
               string.Equals(
                   current.ExecutablePath,
                   ExecutablePath,
                   OperatingSystem.IsWindows()
                       ? StringComparison.OrdinalIgnoreCase
                       : StringComparison.Ordinal) &&
               string.Equals(
                   current.BinaryDigest,
                   CapturedBinaryDigest,
                   StringComparison.Ordinal) &&
               (CapturedUnixFileMode is null ||
                current.UnixFileMode == CapturedUnixFileMode);
    }
}

/// <summary>
/// Immutable execution preparation handed unchanged to the audit barrier and
/// then to dispatch. The planner owns every constructor input; execution code
/// must not recover routing facts by comparing script strings.
/// </summary>
internal sealed record ExecutionPlan
{
    internal ExecutionPlan(
        string originalScript,
        string? executionScript,
        ExecutionDomain? domain,
        ExecutionPath executionPath,
        PreExecutionValidation preExecutionValidation,
        ResolutionContext resolutionContext,
        RequestedExecutionRoute requestedRoute,
        OutputProvenance outputProvenance,
        ImmutableArray<ExecutionPath> permittedFallbacks,
        ExecutionFallbackReason? fallbackReason,
        RtkExecutableIdentity? rtkExecutableIdentity,
        string? workingDirectory = null,
        RtkExecutableIdentity? outputShapingRtkIdentity = null,
        OutputProvenance? directFallbackProvenance = null)
    {
        ArgumentNullException.ThrowIfNull(originalScript);
        var isRtk = executionPath == ExecutionPath.Rtk;
        if (isRtk)
        {
            // An RTK plan is RTK's own rewrite of the submitted text, executed
            // in the warm runspace like any other script. It must therefore
            // carry that rewritten script and must differ from the original;
            // a plan identical to the input has no reason to claim the RTK
            // path.
            if (string.IsNullOrWhiteSpace(executionScript) ||
                string.Equals(executionScript, originalScript, StringComparison.Ordinal) ||
                preExecutionValidation != PreExecutionValidation.None)
            {
                throw new ArgumentException(
                    "RTK execution requires a rewritten script that differs from the original.");
            }
        }
        else if (executionScript is null ||
                 preExecutionValidation != PreExecutionValidation.None)
        {
            throw new ArgumentException(
                "Direct PowerShell execution requires the original script and no validation.",
                nameof(preExecutionValidation));
        }
        if (permittedFallbacks.IsDefault)
            throw new ArgumentException("Fallbacks must be initialized.", nameof(permittedFallbacks));
        if (permittedFallbacks.Any(path => path is not (
                ExecutionPath.PowerShellDirect or ExecutionPath.NativeDirect)))
        {
            throw new ArgumentException(
                "Only exact-semantics direct paths may be fallbacks.",
                nameof(permittedFallbacks));
        }
        if (permittedFallbacks.Distinct().Count() != permittedFallbacks.Length)
            throw new ArgumentException("Fallbacks must be unique.", nameof(permittedFallbacks));
        var hasPowerShellFallback =
            permittedFallbacks.Contains(ExecutionPath.PowerShellDirect);
        if (isRtk && hasPowerShellFallback)
        {
            if (directFallbackProvenance is not (
                    OutputProvenance.PowerShellObjects or OutputProvenance.DirectText))
            {
                throw new ArgumentException(
                    "An RTK PowerShell fallback requires frozen direct-output provenance.",
                    nameof(directFallbackProvenance));
            }
            if (resolutionContext == ResolutionContext.Cold &&
                directFallbackProvenance != OutputProvenance.DirectText)
            {
                throw new ArgumentException(
                    "A cold PowerShell fallback produces direct text.",
                    nameof(directFallbackProvenance));
            }
        }
        else if (directFallbackProvenance is not null)
        {
            throw new ArgumentException(
                "Only an RTK plan with a PowerShell fallback may carry direct-fallback provenance.",
                nameof(directFallbackProvenance));
        }
        if (executionPath is ExecutionPath.Rtk &&
            (rtkExecutableIdentity is null ||
             string.IsNullOrWhiteSpace(rtkExecutableIdentity.ExecutablePath) ||
             !Path.IsPathFullyQualified(rtkExecutableIdentity.ExecutablePath)))
        {
            throw new ArgumentException(
                "RTK execution requires a pinned executable identity.",
                nameof(rtkExecutableIdentity));
        }
        if (executionPath is ExecutionPath.Rtk)
        {
            // Until Slice 4 negotiates a machine-readable RTK capture seam,
            // PTK cannot distinguish filtered output from passthrough.
            if (outputProvenance != OutputProvenance.RtkUnknown)
                throw new ArgumentException(
                    "RTK provenance is unknown without a negotiated capture seam.",
                    nameof(outputProvenance));
        }
        else if (rtkExecutableIdentity is not null ||
                 outputProvenance is OutputProvenance.RtkUnknown or
                     OutputProvenance.RtkFiltered or OutputProvenance.RtkPassthrough)
        {
            throw new ArgumentException(
                "Only RTK execution may carry RTK identity or provenance.",
                nameof(rtkExecutableIdentity));
        }
        if (resolutionContext == ResolutionContext.Cold &&
            executionPath == ExecutionPath.PowerShellDirect &&
            outputProvenance != OutputProvenance.DirectText)
        {
            throw new ArgumentException(
                "Cold direct PowerShell execution produces direct text.",
                nameof(outputProvenance));
        }
        if (outputShapingRtkIdentity is not null &&
            (executionPath != ExecutionPath.PowerShellDirect ||
             outputProvenance != OutputProvenance.PowerShellObjects ||
             string.IsNullOrWhiteSpace(outputShapingRtkIdentity.ExecutablePath) ||
             !Path.IsPathFullyQualified(outputShapingRtkIdentity.ExecutablePath)))
        {
            throw new ArgumentException(
                "An output-shaping RTK identity requires a shaped direct PowerShell plan.",
                nameof(outputShapingRtkIdentity));
        }
        if (fallbackReason is not null &&
            (requestedRoute != RequestedExecutionRoute.Rtk ||
             executionPath != ExecutionPath.PowerShellDirect))
        {
            throw new ArgumentException(
                "An RTK fallback reason requires a requested RTK route and a direct result.",
                nameof(fallbackReason));
        }

        OriginalScript = originalScript;
        ExecutionScript = executionScript;
        Domain = domain;
        ExecutionPath = executionPath;
        PreExecutionValidation = preExecutionValidation;
        ResolutionContext = resolutionContext;
        RequestedRoute = requestedRoute;
        OutputProvenance = outputProvenance;
        PermittedFallbacks = permittedFallbacks;
        FallbackReason = fallbackReason;
        RtkExecutableIdentity = rtkExecutableIdentity;
        WorkingDirectory = workingDirectory;
        OutputShapingRtkIdentity = outputShapingRtkIdentity;
        DirectFallbackProvenance = directFallbackProvenance;
    }

    internal string OriginalScript { get; }
    internal string? ExecutionScript { get; }
    internal ExecutionDomain? Domain { get; }
    internal ExecutionPath ExecutionPath { get; }
    internal PreExecutionValidation PreExecutionValidation { get; }
    internal ResolutionContext ResolutionContext { get; }
    internal RequestedExecutionRoute RequestedRoute { get; }
    internal OutputProvenance OutputProvenance { get; }
    internal ImmutableArray<ExecutionPath> PermittedFallbacks { get; }
    internal ExecutionFallbackReason? FallbackReason { get; }
    internal RtkExecutableIdentity? RtkExecutableIdentity { get; }
    internal string? WorkingDirectory { get; }
    internal RtkExecutableIdentity? OutputShapingRtkIdentity { get; }
    internal OutputProvenance? DirectFallbackProvenance { get; }

    internal string EffectiveRoute => ExecutionPath.ToMachineCode();
}

/// <summary>
/// Immutable execution selected after the prepared plan has crossed its audit
/// barrier. A dispatch may either preserve that plan exactly or consume one of
/// its explicitly permitted, exact-semantics fallbacks. It never invents a
/// route by comparing script text.
/// </summary>
internal sealed record ExecutionDispatch
{
    private ExecutionDispatch(
        ExecutionPlan plan,
        string? executionScript,
        ExecutionPath executionPath,
        OutputProvenance outputProvenance,
        ExecutionFallbackReason? fallbackReason,
        RtkExecutableIdentity? rtkExecutableIdentity)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var followsPlan = executionPath == plan.ExecutionPath;
        if (!followsPlan && !plan.PermittedFallbacks.Contains(executionPath))
            throw new InvalidOperationException("The dispatch path was not authorized by the plan.");

        if (followsPlan)
        {
            if (!string.Equals(executionScript, plan.ExecutionScript, StringComparison.Ordinal) ||
                outputProvenance != plan.OutputProvenance ||
                fallbackReason != plan.FallbackReason ||
                rtkExecutableIdentity != plan.RtkExecutableIdentity)
            {
                throw new InvalidOperationException(
                    "A same-path dispatch must preserve every planned execution fact.");
            }
        }
        else
        {
            if (!string.Equals(executionScript, plan.OriginalScript, StringComparison.Ordinal))
                throw new InvalidOperationException("A direct fallback must execute the exact original script.");
            if (executionPath != ExecutionPath.PowerShellDirect ||
                outputProvenance != plan.DirectFallbackProvenance ||
                fallbackReason is null ||
                rtkExecutableIdentity is not null)
            {
                throw new InvalidOperationException(
                    "A fallback must carry exact direct-path provenance and a truthful reason.");
            }
        }

        Plan = plan;
        ExecutionScript = executionScript;
        ExecutionPath = executionPath;
        OutputProvenance = outputProvenance;
        FallbackReason = fallbackReason;
        RtkExecutableIdentity = rtkExecutableIdentity;
    }

    internal ExecutionPlan Plan { get; }
    internal string? ExecutionScript { get; }
    internal ExecutionPath ExecutionPath { get; }
    internal OutputProvenance OutputProvenance { get; }
    internal ExecutionFallbackReason? FallbackReason { get; }
    internal RtkExecutableIdentity? RtkExecutableIdentity { get; }
    internal string? WorkingDirectory => Plan.WorkingDirectory;
    internal RtkExecutableIdentity? OutputShapingRtkIdentity =>
        ExecutionPath == ExecutionPath.PowerShellDirect
            ? Plan.OutputShapingRtkIdentity
            : null;
    internal ExecutionDomain? Domain => Plan.Domain;
    internal PreExecutionValidation PreExecutionValidation => Plan.PreExecutionValidation;
    internal ResolutionContext ResolutionContext => Plan.ResolutionContext;
    internal RequestedExecutionRoute RequestedRoute => Plan.RequestedRoute;
    internal ImmutableArray<ExecutionPath> PermittedFallbacks => Plan.PermittedFallbacks;
    internal string EffectiveRoute => ExecutionPath.ToMachineCode();
    internal bool IsFallback => ExecutionPath != Plan.ExecutionPath;

    internal static ExecutionDispatch FromPlan(ExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new ExecutionDispatch(
            plan,
            plan.ExecutionScript,
            plan.ExecutionPath,
            plan.OutputProvenance,
            plan.FallbackReason,
            plan.RtkExecutableIdentity);
    }

    internal static ExecutionDispatch RtkUnavailableFallback(ExecutionPlan plan)
        => RtkPreStartFallback(
            plan,
            ExecutionFallbackReason.RtkExecutableBecameUnavailable);

    internal static ExecutionDispatch RtkPreStartFallback(
        ExecutionPlan plan,
        ExecutionFallbackReason reason)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.ExecutionPath != ExecutionPath.Rtk ||
            !plan.PermittedFallbacks.Contains(ExecutionPath.PowerShellDirect))
        {
            throw new InvalidOperationException(
                "Only an RTK plan with an authorized direct fallback may fall back.");
        }
        if (reason is not (
                ExecutionFallbackReason.RtkExecutableBecameUnavailable or
                ExecutionFallbackReason.RtkExecutionPreparationFailed or
                ExecutionFallbackReason.RtkTargetResolutionChanged))
        {
            throw new InvalidOperationException(
                "Only a proven pre-start RTK failure may consume the exact fallback.");
        }

        return new ExecutionDispatch(
            plan,
            plan.OriginalScript,
            ExecutionPath.PowerShellDirect,
            plan.DirectFallbackProvenance ?? throw new InvalidOperationException(
                "The RTK plan did not freeze direct-fallback provenance."),
            reason,
            rtkExecutableIdentity: null);
    }
}

internal static class ExecutionPlanMachineCodes
{
    internal static string ToMachineCode(this ExecutionDomain value) => value switch
    {
        ExecutionDomain.PowerShell => "powershell",
        ExecutionDomain.NativeTerminal => "native_terminal",
        ExecutionDomain.MixedDataflow => "mixed_dataflow",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    internal static string ToMachineCode(this ExecutionPath value) => value switch
    {
        ExecutionPath.PowerShellDirect => "powershell_direct",
        ExecutionPath.Rtk => "rtk",
        ExecutionPath.NativeDirect => "native_direct",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    internal static string ToMachineCode(this RequestedExecutionRoute value) => value switch
    {
        RequestedExecutionRoute.Auto => "auto",
        RequestedExecutionRoute.PowerShell => "pwsh",
        RequestedExecutionRoute.Rtk => "rtk",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    internal static string ToMachineCode(this OutputProvenance value) => value switch
    {
        OutputProvenance.PowerShellObjects => "powershell_objects",
        OutputProvenance.DirectText => "direct_text",
        OutputProvenance.RtkUnknown => "rtk_unknown",
        OutputProvenance.RtkFiltered => "rtk_filtered",
        OutputProvenance.RtkPassthrough => "rtk_passthrough",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    internal static string ToMachineCode(this ExecutionFallbackReason value) => value switch
    {
        ExecutionFallbackReason.RtkExecutableUnavailable => "rtk_executable_unavailable",
        ExecutionFallbackReason.RtkExecutableBecameUnavailable => "rtk_executable_became_unavailable",
        ExecutionFallbackReason.RtkIneligibleShape => "rtk_ineligible_shape",
        ExecutionFallbackReason.RtkSelfInvocation => "rtk_self_invocation",
        ExecutionFallbackReason.RtkResolutionNotApplication => "rtk_resolution_not_application",
        ExecutionFallbackReason.RtkFidelityExclusion => "rtk_fidelity_exclusion",
        ExecutionFallbackReason.RtkExecutionPreparationFailed => "rtk_execution_preparation_failed",
        ExecutionFallbackReason.RtkTargetResolutionChanged => "rtk_target_resolution_changed",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
