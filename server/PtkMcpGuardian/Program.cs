using PtkMcpGuardian.Standalone.Fake;

namespace PtkMcpGuardian;

internal static class Program
{
    internal const int UsageExitCode = 64;
    internal const int SoftwareExitCode = 70;
    internal const string UnsupportedModeMessage =
        "ptk guardian: this build supports only the explicit --fake-host development mode.";
    internal const string RuntimeFailureMessage =
        "ptk guardian: the fake-host development mode terminated unexpectedly.";

    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!IsFakeHostMode(args))
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
        R3FakeGuardianComposition? composition = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(publicInput);
        ArgumentNullException.ThrowIfNull(publicOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (!IsFakeHostMode(arguments))
            return RejectUnsupportedMode(standardError);

        await using var selectedComposition = composition ??
            R3FakeGuardianComposition.Create();
        try
        {
            await selectedComposition
                .RunAsync(publicInput, publicOutput, cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            standardError.WriteLine(RuntimeFailureMessage);
            return SoftwareExitCode;
        }
    }

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
