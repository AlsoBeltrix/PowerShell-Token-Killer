using System.Security.Cryptography;
using System.Threading.Channels;
using PtkSharedContracts;

namespace PtkMcpGuardian.Standalone.Fake;

/// <summary>
/// A bounded in-memory one-way transport. Every accepted write is copied into
/// one owned buffer, and that buffer is zeroed immediately after consumption
/// or disposal.
/// </summary>
internal sealed class R3BoundedOneWayStream : Stream
{
    internal const int DefaultCapacity = 8;
    internal const int MaximumWriteBytes = ContractLimits.MaximumEncodedFrameBytes + 1;

    private readonly object _sync = new();
    private readonly Channel<byte[]> _chunks;
    private readonly SemaphoreSlim _bufferSlots;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();
    private readonly Action<byte[]>? _retiredBufferObserver;

    private byte[]? _current;
    private int _currentOffset;
    private int _bufferedChunkCount;
    private int _maximumBufferedChunkCount;
    private bool _writingCompleted;
    private bool _writesRejected;
    private bool _isDisposed;
    private NextWriteDirective? _nextWrite;

    internal R3BoundedOneWayStream(
        int capacity = DefaultCapacity,
        Action<byte[]>? retiredBufferObserver = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        _bufferSlots = new SemaphoreSlim(capacity, capacity);
        _retiredBufferObserver = retiredBufferObserver;
    }

    internal int BufferedChunkCount => Volatile.Read(ref _bufferedChunkCount);

    internal int MaximumBufferedChunkCount => Volatile.Read(ref _maximumBufferedChunkCount);

    internal bool IsDisposed
    {
        get { lock (_sync) return _isDisposed; }
    }

    internal R3FakeHostBarrier ArmNextWrite(bool failAfterRelease = false)
    {
        lock (_sync)
        {
            ThrowIfDisposedLocked();
            if (_writingCompleted || _writesRejected)
                throw new InvalidOperationException("The transport no longer accepts writes.");
            if (_nextWrite is not null)
                throw new InvalidOperationException("A next-write directive is already armed.");

            var barrier = new R3FakeHostBarrier();
            _nextWrite = new NextWriteDirective(barrier, failAfterRelease);
            return barrier;
        }
    }

    internal void CompleteWriting()
    {
        lock (_sync)
        {
            if (_isDisposed || _writingCompleted || _writesRejected)
                return;
            _writingCompleted = true;
        }
        _chunks.Writer.TryComplete();
    }

    internal void RejectWrites()
    {
        lock (_sync)
        {
            if (_isDisposed || _writingCompleted || _writesRejected)
                return;
            _writesRejected = true;
        }
        _chunks.Writer.TryComplete(new IOException("The fake transport was closed."));
    }

    public override bool CanRead => !IsDisposed;

    public override bool CanSeek => false;

    public override bool CanWrite
    {
        get
        {
            lock (_sync)
                return !_isDisposed && !_writingCompleted && !_writesRejected;
        }
    }

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        lock (_sync) ThrowIfDisposedLocked();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush();
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
            return 0;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposed.Token);
        await _readGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_sync) ThrowIfDisposedLocked();
            if (_current is null)
            {
                try
                {
                    _current = await _chunks.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                    _currentOffset = 0;
                }
                catch (ChannelClosedException exception) when (exception.InnerException is null)
                {
                    return 0;
                }
                catch (ChannelClosedException exception)
                {
                    throw new IOException("The fake transport was closed.", exception.InnerException);
                }
            }

            var available = _current.Length - _currentOffset;
            var count = Math.Min(buffer.Length, available);
            _current.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            if (_currentOffset == _current.Length)
                RetireCurrent();
            return count;
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(R3BoundedOneWayStream));
        }
        finally
        {
            _readGate.Release();
        }
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
            return;
        if (buffer.Length > MaximumWriteBytes)
            throw new ArgumentOutOfRangeException(
                nameof(buffer),
                $"A fake transport write cannot exceed {MaximumWriteBytes} bytes.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposed.Token);
        await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        var ownsSlot = false;
        var counted = false;
        byte[]? owned = null;
        try
        {
            NextWriteDirective? directive;
            lock (_sync)
            {
                ThrowIfNotWritableLocked();
                directive = _nextWrite;
                _nextWrite = null;
            }
            if (directive is not null)
            {
                await directive.Barrier.ReachAndWaitAsync(linked.Token).ConfigureAwait(false);
                if (directive.FailAfterRelease)
                    throw new IOException("The armed fake transport write failed.");
            }

            await _bufferSlots.WaitAsync(linked.Token).ConfigureAwait(false);
            ownsSlot = true;
            lock (_sync) ThrowIfNotWritableLocked();

            owned = buffer.ToArray();
            var buffered = Interlocked.Increment(ref _bufferedChunkCount);
            counted = true;
            UpdateMaximum(buffered);
            await _chunks.Writer.WriteAsync(owned, linked.Token).ConfigureAwait(false);
            owned = null;
            ownsSlot = false;
            counted = false;
        }
        catch (ChannelClosedException exception)
        {
            throw new IOException("The fake transport was closed.", exception.InnerException ?? exception);
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(R3BoundedOneWayStream));
        }
        finally
        {
            if (owned is not null)
            {
                Retire(owned, counted);
                if (counted)
                    ownsSlot = false;
            }
            if (ownsSlot)
                _bufferSlots.Release();
            _writeGate.Release();
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;

        lock (_sync)
        {
            if (_isDisposed)
                return;
            _isDisposed = true;
            _writesRejected = true;
        }

        _disposed.Cancel();
        _chunks.Writer.TryComplete();
        _writeGate.Wait();
        _readGate.Wait();
        try
        {
            if (_current is not null)
                RetireCurrent();
            while (_chunks.Reader.TryRead(out var chunk))
                Retire(chunk, counted: true);
        }
        finally
        {
            _readGate.Release();
            _writeGate.Release();
            _disposed.Dispose();
            base.Dispose(disposing);
        }
    }

    private void RetireCurrent()
    {
        var current = _current!;
        _current = null;
        _currentOffset = 0;
        Retire(current, counted: true);
    }

    private void Retire(byte[] buffer, bool counted)
    {
        CryptographicOperations.ZeroMemory(buffer);
        try
        {
            _retiredBufferObserver?.Invoke(buffer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (counted)
            {
                Interlocked.Decrement(ref _bufferedChunkCount);
                _bufferSlots.Release();
            }
        }
    }

    private void UpdateMaximum(int buffered)
    {
        var observed = Volatile.Read(ref _maximumBufferedChunkCount);
        while (buffered > observed)
        {
            var prior = Interlocked.CompareExchange(
                ref _maximumBufferedChunkCount,
                buffered,
                observed);
            if (prior == observed)
                return;
            observed = prior;
        }
    }

    private void ThrowIfDisposedLocked()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(R3BoundedOneWayStream));
    }

    private void ThrowIfNotWritableLocked()
    {
        ThrowIfDisposedLocked();
        if (_writingCompleted || _writesRejected)
            throw new IOException("The fake transport no longer accepts writes.");
    }

    private sealed record NextWriteDirective(
        R3FakeHostBarrier Barrier,
        bool FailAfterRelease);
}
