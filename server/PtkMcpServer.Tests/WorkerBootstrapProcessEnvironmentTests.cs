using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

[Collection(WindowsProcessCreationCollection.Name)]
public sealed class WorkerBootstrapProcessEnvironmentTests
{
    [Fact]
    public void Default_source_captures_and_removes_all_reserved_variables()
    {
        var priorRequest = Environment.GetEnvironmentVariable(
            WorkerBootstrapEnvironment.RequestHandle);
        var priorEvent = Environment.GetEnvironmentVariable(
            WorkerBootstrapEnvironment.EventHandle);
        var priorBootId = Environment.GetEnvironmentVariable(
            WorkerBootstrapEnvironment.BootId);
        try
        {
            Environment.SetEnvironmentVariable(
                WorkerBootstrapEnvironment.RequestHandle,
                "101");
            Environment.SetEnvironmentVariable(
                WorkerBootstrapEnvironment.EventHandle,
                "202");
            Environment.SetEnvironmentVariable(
                WorkerBootstrapEnvironment.BootId,
                "27e13a09-3106-4c60-936d-2f6e165f54ad");

            var values = WorkerBootstrapCapture.CaptureAndRemove();

            Assert.Equal(
                new WorkerBootstrapValues(
                    "101",
                    "202",
                    "27e13a09-3106-4c60-936d-2f6e165f54ad"),
                values);
            Assert.Null(Environment.GetEnvironmentVariable(
                WorkerBootstrapEnvironment.RequestHandle));
            Assert.Null(Environment.GetEnvironmentVariable(
                WorkerBootstrapEnvironment.EventHandle));
            Assert.Null(Environment.GetEnvironmentVariable(
                WorkerBootstrapEnvironment.BootId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                WorkerBootstrapEnvironment.RequestHandle,
                priorRequest);
            Environment.SetEnvironmentVariable(
                WorkerBootstrapEnvironment.EventHandle,
                priorEvent);
            Environment.SetEnvironmentVariable(
                WorkerBootstrapEnvironment.BootId,
                priorBootId);
        }
    }
}
