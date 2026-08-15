using PtkSiemReceiver.Configuration;

namespace PtkSiemReceiver.Tests;

public sealed class ReceiverConfigurationPathTests
{
    [Fact]
    public void Environment_path_remains_the_foreground_default()
    {
        Assert.Equal("/receiver/config.json", ReceiverConfigurationPath.Resolve(
            [],
            "/receiver/config.json"));
    }

    [Fact]
    public void Explicit_config_argument_supports_native_service_command_lines()
    {
        Assert.Equal("/service/config.json", ReceiverConfigurationPath.Resolve(
            ["--config", "/service/config.json"],
            "/ignored/environment.json"));
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--config")]
    public void Unknown_or_incomplete_arguments_fail_closed(string argument)
    {
        var failure = Assert.Throws<SiemReceiverConfigurationException>(() =>
            ReceiverConfigurationPath.Resolve([argument], null));

        Assert.Equal("config_argument", failure.FailureCode);
    }
}
