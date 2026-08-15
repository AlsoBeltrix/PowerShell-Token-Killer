using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Ingest;

string? configurationPath;
try
{
    configurationPath = ReceiverConfigurationPath.Resolve(
        args,
        Environment.GetEnvironmentVariable("PTK_SIEM_CONFIG"));
}
catch (SiemReceiverConfigurationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
if (string.IsNullOrWhiteSpace(configurationPath))
{
    Console.Error.WriteLine(
        "siem_receiver_configuration_invalid: config_env — pass --config PATH or set " +
        "PTK_SIEM_CONFIG to the fully qualified receiver configuration JSON path.");
    return 1;
}

SiemReceiverOptions options;
try
{
    options = SiemReceiverConfigurationLoader.Load(configurationPath);
}
catch (SiemReceiverConfigurationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

try
{
    await using var application = ReceiverApplication.Build(options, []);
    await application.RunAsync();
    return 0;
}
catch (SiemReceiverStartupException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

internal static class ReceiverConfigurationPath
{
    internal static string? Resolve(string[] args, string? environmentPath)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0) return environmentPath;
        if (args.Length == 2 &&
            string.Equals(args[0], "--config", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(args[1]))
        {
            return args[1];
        }
        throw new SiemReceiverConfigurationException("config_argument");
    }
}
