using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class UnixWorkerBootstrapTests
{
    [Fact]
    public void Bootstrap_marks_duplicates_closes_originals_and_owns_streams()
    {
        var native = new RecordingNative();

        using var streams = UnixWorkerBootstrap.Open(
            new WorkerBootstrapValues("3", "4"),
            native,
            isUnix: () => true);

        Assert.Equal(
            new[]
            {
                "cloexec:3", "cloexec:4", "duplicate:3", "duplicate:4",
                "close:3", "close:4", "access:13", "access:14",
                "stream:13:Read", "stream:14:Write",
            },
            native.Calls);
        Assert.Same(native.RequestStream, streams.RequestStream);
        Assert.Same(native.EventStream, streams.EventStream);
        Assert.Empty(native.OpenDescriptors);
    }

    [Fact]
    public void Bootstrap_rejects_wrong_direction_and_closes_both_duplicates()
    {
        var native = new RecordingNative
        {
            RequestAccess = FileAccess.Write,
        };

        var exception = Assert.Throws<WorkerBootstrapException>(() =>
            UnixWorkerBootstrap.Open(
                new WorkerBootstrapValues("3", "4"),
                native,
                isUnix: () => true));

        Assert.Equal("handle_direction_invalid", exception.DetailCode);
        Assert.Equal(new[] { 13, 14 }, native.ClosedDuplicates.Order());
        Assert.Empty(native.OpenDescriptors);
    }

    [Fact]
    public void Bootstrap_rejects_environment_handle_injection_before_native_access()
    {
        var native = new RecordingNative();

        var exception = Assert.Throws<WorkerBootstrapException>(() =>
            UnixWorkerBootstrap.Open(
                new WorkerBootstrapValues("03", "4"),
                native,
                isUnix: () => true));

        Assert.Equal("handle_invalid", exception.DetailCode);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void Bootstrap_rejects_missing_broker_marker_before_native_access()
    {
        var native = new RecordingNative();

        var exception = Assert.Throws<WorkerBootstrapException>(() =>
            UnixWorkerBootstrap.Open(
                new WorkerBootstrapValues(null, null),
                native,
                isUnix: () => true));

        Assert.Equal("handle_missing", exception.DetailCode);
        Assert.Empty(native.Calls);
    }

    private sealed class RecordingNative : IUnixWorkerBootstrapNative
    {
        internal List<string> Calls { get; } = [];
        internal HashSet<int> OpenDescriptors { get; } = [];
        internal List<int> ClosedDuplicates { get; } = [];
        internal MemoryStream RequestStream { get; } = new();
        internal MemoryStream EventStream { get; } = new();
        internal FileAccess RequestAccess { get; init; } = FileAccess.Read;

        public void SetCloseOnExec(int descriptor) =>
            Calls.Add($"cloexec:{descriptor}");

        public int DuplicateCloseOnExec(int descriptor)
        {
            Calls.Add($"duplicate:{descriptor}");
            var duplicate = descriptor + 10;
            OpenDescriptors.Add(duplicate);
            return duplicate;
        }

        public FileAccess GetAccess(int descriptor)
        {
            Calls.Add($"access:{descriptor}");
            return descriptor == 13 ? RequestAccess : FileAccess.Write;
        }

        public void Close(int descriptor)
        {
            Calls.Add($"close:{descriptor}");
            if (OpenDescriptors.Remove(descriptor))
                ClosedDuplicates.Add(descriptor);
        }

        public Stream CreateStream(int descriptor, FileAccess access)
        {
            Calls.Add($"stream:{descriptor}:{access}");
            Assert.True(OpenDescriptors.Remove(descriptor));
            return access == FileAccess.Read ? RequestStream : EventStream;
        }
    }
}
