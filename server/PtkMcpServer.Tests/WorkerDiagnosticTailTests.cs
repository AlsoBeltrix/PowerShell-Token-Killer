using System.Text;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerDiagnosticTailTests
{
    private const string Diagnostic =
        "ptk_worker_exit kind=runtime_failure detail=runtime_failure\n";

    [Fact]
    public async Task Retains_the_worker_exit_diagnostic()
    {
        var tail = new WorkerDiagnosticTail();

        await tail.DrainAsync(Stream(Diagnostic));

        Assert.Equal(Diagnostic.TrimEnd('\n'), tail.Text);
    }

    [Fact]
    public async Task Reports_absent_when_the_worker_wrote_nothing()
    {
        var tail = new WorkerDiagnosticTail();

        await tail.DrainAsync(Stream(string.Empty));

        Assert.Null(tail.Text);
    }

    [Fact]
    public async Task Keeps_only_the_final_bounded_bytes_of_a_long_stream()
    {
        var tail = new WorkerDiagnosticTail();
        var noise = new string('x', 64 * 1024);

        await tail.DrainAsync(Stream(noise + Diagnostic));

        // The bound is the worker's own diagnostic cap. Without it, a worker
        // that floods this stream would size the supervisor's retention.
        var text = tail.Text;
        Assert.NotNull(text);
        Assert.True(
            Encoding.ASCII.GetByteCount(text) <=
                WorkerProcessExit.MaximumDiagnosticBytes,
            $"retained {Encoding.ASCII.GetByteCount(text)} bytes");
        Assert.EndsWith(Diagnostic.TrimEnd('\n'), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keeps_the_tail_across_many_small_writes()
    {
        var tail = new WorkerDiagnosticTail();
        using var stream = new ChunkedStream(
            [.. Enumerable
                .Repeat("filler line\n", 200)
                .Append(Diagnostic)
                .Select(Encoding.ASCII.GetBytes)]);

        await tail.DrainAsync(stream);

        var text = tail.Text;
        Assert.NotNull(text);
        Assert.EndsWith(Diagnostic.TrimEnd('\n'), text, StringComparison.Ordinal);
        Assert.True(
            Encoding.ASCII.GetByteCount(text) <=
                WorkerProcessExit.MaximumDiagnosticBytes,
            $"retained {Encoding.ASCII.GetByteCount(text)} bytes");
    }

    [Fact]
    public async Task Reports_absent_when_the_tail_is_not_the_workers_own_diagnostic()
    {
        var tail = new WorkerDiagnosticTail();

        // Non-ASCII means the caller's command is talking on this stream, not
        // the worker's exit path. Surfacing it would put arbitrary executed
        // output inside a supervisor-authored failure line.
        await tail.DrainAsync(new MemoryStream([0x41, 0xff, 0x42]));

        Assert.Null(tail.Text);
    }

    [Fact]
    public async Task Reports_absent_when_the_tail_is_only_whitespace()
    {
        var tail = new WorkerDiagnosticTail();

        await tail.DrainAsync(Stream("\r\n \r\n"));

        Assert.Null(tail.Text);
    }

    [Fact]
    public async Task A_failing_read_keeps_what_already_arrived()
    {
        var tail = new WorkerDiagnosticTail();
        using var stream = new FailAfterFirstReadStream(
            Encoding.ASCII.GetBytes(Diagnostic));

        // Never throws: this runs while a worker is already dying.
        await tail.DrainAsync(stream);

        Assert.Equal(Diagnostic.TrimEnd('\n'), tail.Text);
    }

    private static MemoryStream Stream(string text) =>
        new(Encoding.ASCII.GetBytes(text));

    private sealed class ChunkedStream(byte[][] chunks) : Stream
    {
        private int _index;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_index >= chunks.Length)
                return 0;
            var chunk = chunks[_index++];
            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class FailAfterFirstReadStream(byte[] first) : Stream
    {
        private bool _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_read)
                throw new IOException("The worker pipe broke.");
            _read = true;
            first.CopyTo(buffer);
            return first.Length;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
