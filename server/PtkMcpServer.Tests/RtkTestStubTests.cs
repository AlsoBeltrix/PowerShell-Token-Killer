using System.Diagnostics;

namespace PtkMcpServer.Tests;

public sealed class RtkTestStubTests
{
    [Fact]
    public async Task Passthrough_forwards_exact_target_streams_and_exit_code()
    {
        var root = Directory.CreateTempSubdirectory("ptk-rtk-passthrough-");
        try
        {
            var stub = RtkTestStub.CreatePassthrough(root.FullName).Path;
            var target = OperatingSystem.IsWindows()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "cmd.exe")
                : File.Exists("/bin/sh") ? "/bin/sh" : "/usr/bin/sh";
            var command = OperatingSystem.IsWindows()
                ? "echo PTK_STUB_STDOUT&echo PTK_STUB_STDERR 1>&2&exit /b 7"
                : "printf '%s\\n' PTK_STUB_STDOUT; printf '%s\\n' PTK_STUB_STDERR 1>&2; exit 7";
            var startInfo = new ProcessStartInfo
            {
                FileName = stub,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(target);
            if (OperatingSystem.IsWindows())
            {
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/s");
                startInfo.ArgumentList.Add("/c");
            }
            else
            {
                startInfo.ArgumentList.Add("-c");
            }
            startInfo.ArgumentList.Add(command);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(7, process.ExitCode);
            Assert.Contains("PTK_STUB_STDOUT", await stdout, StringComparison.Ordinal);
            Assert.Contains("PTK_STUB_STDERR", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
