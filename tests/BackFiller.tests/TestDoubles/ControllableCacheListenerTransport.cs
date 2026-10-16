using System.Threading.Channels;
using VectorNNTP.BackFiller.Listener;

namespace VectorNNTP.BackFiller.Tests.TestDoubles
{
    /// <summary>
    /// Test transport that can keep delivering inbound bytes after forcing writer-side failure.
    /// </summary>
    internal sealed class ControllableCacheListenerTransport : ICacheListenerTransport
    {
        /// <summary>Unbounded inbound queue from test-enqueued client bytes to the session reader.</summary>
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        /// <summary>Protects write-side state, recorded frames, and write gates.</summary>
        private readonly object _writeGate = new();
        /// <summary>Server-to-client frames captured on successful writes.</summary>
        private readonly List<byte[]> _written = [];
        /// <summary>When set, blocked writes await this task until released or faulted.</summary>
        private TaskCompletionSource? _blockWrites;
        /// <summary>Exception surfaced on the next or currently blocked write.</summary>
        private Exception? _writeFault;
        /// <summary>Successful writes allowed before automatic write blocking begins.</summary>
        private int _successfulWritesAllowed = int.MaxValue;
        /// <summary>Successful writes completed since the last gate reset.</summary>
        private int _successfulWritesCompleted;
        /// <summary>1 after <see cref="DisposeAsync"/>; reads and writes throw.</summary>
        private int _disposed;
        /// <summary>Writes that entered the block path awaiting release.</summary>
        private int _blockedWrites;
        /// <summary>Total <see cref="WriteAsync"/> invocations.</summary>
        private int _writeCalls;
        /// <summary>Total <see cref="ReadAsync"/> invocations.</summary>
        private int _readCalls;

        /// <summary>Observed outbound write call count.</summary>
        public int WriteCalls => Volatile.Read(ref _writeCalls);

        /// <summary>Writes that blocked waiting on the write gate.</summary>
        public int BlockedWrites => Volatile.Read(ref _blockedWrites);

        /// <summary>Observed inbound read call count.</summary>
        public int ReadCalls => Volatile.Read(ref _readCalls);

        /// <summary>Copy of captured outbound frames in write order.</summary>
        public IReadOnlyList<byte[]> WrittenFrames
        {
            get
            {
                lock (_writeGate)
                {
                    return _written.ToArray();
                }
            }
        }

        /// <summary>Queues bytes for the session reader; no-op when empty or disposed.</summary>
        /// <param name="bytes">Next inbound chunk; copied into the channel.</param>
        public void EnqueueInbound(ReadOnlyMemory<byte> bytes)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (bytes.IsEmpty)
            {
                return;
            }

            _ = _inbound.Writer.TryWrite(bytes.ToArray());
        }

        /// <summary>Completes the inbound writer so reads eventually return zero unless faulted.</summary>
        public void CompleteInbound() => _ = _inbound.Writer.TryComplete();

        /// <summary>After <paramref name="count"/> successful writes, further writes block until fail/release.</summary>
        public void AllowSuccessfulWritesThenBlock(int count)
        {
            lock (_writeGate)
            {
                _successfulWritesAllowed = count;
                _blockWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        /// <summary>Fails the next (or currently blocked) write with <paramref name="exception"/>.</summary>
        public void FailWrites(Exception? exception = null)
        {
            TaskCompletionSource? blocked;
            lock (_writeGate)
            {
                _writeFault = exception ?? new IOException("forced writer transport failure");
                blocked = _blockWrites;
                _blockWrites = null;
            }

            blocked?.TrySetException(_writeFault);
        }

        /// <summary>Fails reads with the supplied exception (reader-side terminal failure).</summary>
        public void FailReads(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            _ = _inbound.Writer.TryComplete(exception);
        }

        /// <inheritdoc />
        /// <remarks>Propagates inner exceptions from a faulted channel; partial frames drop trailing bytes.</remarks>
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            Interlocked.Increment(ref _readCalls);

            byte[] chunk;
            try
            {
                chunk = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }

            var copy = Math.Min(buffer.Length, chunk.Length);
            chunk.AsSpan(0, copy).CopyTo(buffer.Span);
            if (copy < chunk.Length)
            {
                // Remainder is dropped; tests enqueue whole frames sized under the read buffer.
            }

            return copy;
        }

        /// <inheritdoc />
        /// <remarks>Honors write limits, blocking, faults, and records full buffers on success.</remarks>
        public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            Interlocked.Increment(ref _writeCalls);
            if (buffer.IsEmpty)
            {
                return 0;
            }

            Task? block;
            lock (_writeGate)
            {
                if (_writeFault is not null)
                {
                    throw _writeFault;
                }

                if (_successfulWritesCompleted >= _successfulWritesAllowed)
                {
                    _blockWrites ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    block = _blockWrites.Task;
                    Interlocked.Increment(ref _blockedWrites);
                }
                else
                {
                    _successfulWritesCompleted++;
                    _written.Add(buffer.ToArray());
                    return buffer.Length;
                }
            }

            try
            {
                await block.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                lock (_writeGate)
                {
                    if (_writeFault is not null)
                    {
                        throw _writeFault;
                    }
                }

                throw;
            }

            lock (_writeGate)
            {
                if (_writeFault is not null)
                {
                    throw _writeFault;
                }

                _written.Add(buffer.ToArray());
                return buffer.Length;
            }
        }

        /// <inheritdoc />
        /// <remarks>Completes inbound and unblocks writes with <see cref="ObjectDisposedException"/>.</remarks>
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return ValueTask.CompletedTask;
            }

            _ = _inbound.Writer.TryComplete();
            FailWrites(new ObjectDisposedException(nameof(ControllableCacheListenerTransport)));
            return ValueTask.CompletedTask;
        }
    }
}
