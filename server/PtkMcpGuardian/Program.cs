using PtkMcpGuardian.Package;
using PtkMcpGuardian.Standalone;
using PtkMcpGuardian.Standalone.Fake;

namespace PtkMcpGuardian;

internal static class Program
{
    internal const int UsageExitCode = 64;
    internal const int SoftwareExitCode = 70;
    internal const string UnsupportedModeMessage =
        "ptk guardian: expected no arguments or the exact --fake-host development mode.";
    internal const string RuntimeFailureMessage =
        "ptk guardian: the fake-host development mode terminated unexpectedly.";
    internal const string ProductionRuntimeFailureMessage =
        "ptk guardian: production startup or runtime failed.";

    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!IsSupportedMode(args))
            return RejectUnsupportedMode(Console.Error);

        using var publicInput = Console.OpenStandardInput();
        using var publicOutput = Console.OpenStandardOutput();
        return await RunAsync(
                args,
                publicInput,
                publicOutput,
                Console.Error)
            .ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        Stream publicInput,
        Stream publicOutput,
        TextWriter standardError,
        R3FakeGuardianComposition? fakeComposition = null,
        ProductionGuardianComposition? productionComposition = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(publicInput);
        ArgumentNullException.ThrowIfNull(publicOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (!IsSupportedMode(arguments))
            return RejectUnsupportedMode(standardError);

        try
        {
            if (IsFakeHostMode(arguments))
            {
                if (productionComposition is not null)
                    throw new ArgumentException("Production composition cannot run in fake mode.");
                await using var selectedFake = fakeComposition ??
                    R3FakeGuardianComposition.Create();
                await selectedFake
                    .RunAsync(publicInput, publicOutput, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                if (fakeComposition is not null)
                    throw new ArgumentException("Fake composition cannot run in production mode.");
                await using var selectedProduction = productionComposition ??
                    ProductionGuardianComposition.Create(CurrentMatchedPackage.Load());
                await selectedProduction
                    .RunAsync(publicInput, publicOutput, cancellationToken)
                    .ConfigureAwait(false);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            standardError.WriteLine(IsFakeHostMode(arguments)
                ? RuntimeFailureMessage
                : ProductionRuntimeFailureMessage);
            return SoftwareExitCode;
        }
    }

    private static bool IsSupportedMode(IReadOnlyList<string> arguments) =>
        IsProductionMode(arguments) || IsFakeHostMode(arguments);

    private static bool IsProductionMode(IReadOnlyList<string> arguments) =>
        arguments.Count == 0;

    private static bool IsFakeHostMode(IReadOnlyList<string> arguments) =>
        arguments.Count == 1 &&
        StringComparer.Ordinal.Equals(arguments[0], "--fake-host");

    private static int RejectUnsupportedMode(TextWriter standardError)
    {
        standardError.WriteLine(UnsupportedModeMessage);
        return UsageExitCode;
    }

    private static bool IsFatalRuntimeException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException;
}
