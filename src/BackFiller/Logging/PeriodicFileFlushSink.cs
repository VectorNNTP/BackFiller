using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.File;

namespace VectorNNTP.BackFiller.Logging
{
    /// <summary>
    /// Flushes an <see cref="IFlushableFileSink"/> on an interval and again when this sink is disposed.
    /// </summary>
    /// <remarks>
    /// Serilog disposes this object with the logger. <see cref="Dispose"/> stops the timer, waits until an
    /// in-flight callback has left the flush lock, flushes any remaining buffered write, then disposes the
    /// inner sink. A second <see cref="Dispose"/> does nothing.
    /// </remarks>
    internal sealed class PeriodicFileFlushSink : ILogEventSink, IDisposable
    {
        private readonly object _gate = new();
        private readonly ILogEventSink _sink;
        private readonly IFlushableFileSink _flushable;
        private readonly Timer _timer;
        private int _flushRequired;
        private int _disposed;

        /// <summary>Wraps <paramref name="sink"/> and flushes it on <paramref name="flushInterval"/>.</summary>
        /// <param name="sink">Inner sink. It must implement <see cref="IFlushableFileSink"/>.</param>
        /// <param name="flushInterval">Delay before the first flush and between later flushes.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="sink"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="sink"/> is not an <see cref="IFlushableFileSink"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="flushInterval"/> is not positive.</exception>
        internal PeriodicFileFlushSink(ILogEventSink sink, TimeSpan flushInterval)
        {
            ArgumentNullException.ThrowIfNull(sink);
            if (flushInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(flushInterval), flushInterval, "Flush interval must be positive.");
            }

            if (sink is not IFlushableFileSink flushable)
            {
                throw new ArgumentException("The sink must implement IFlushableFileSink.", nameof(sink));
            }

            _sink = sink;
            _flushable = flushable;
            _timer = new Timer(OnTick, null, flushInterval, flushInterval);
        }

        /// <summary>Writes one event and records that a later flush is required.</summary>
        /// <param name="logEvent">Event forwarded to the inner sink.</param>
        public void Emit(LogEvent logEvent)
        {
            _sink.Emit(logEvent);
            Interlocked.Exchange(ref _flushRequired, 1);
        }

        /// <summary>Stops the timer, flushes the last buffered write, and disposes the inner sink.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            lock (_gate)
            {
                FlushRequired();
                (_sink as IDisposable)?.Dispose();
            }

            _timer.Dispose();
        }

        private void OnTick(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed != 0)
                {
                    return;
                }

                FlushRequired();
            }
        }

        private void FlushRequired()
        {
            if (Interlocked.Exchange(ref _flushRequired, 0) != 1)
            {
                return;
            }

            _flushable.FlushToDisk();
        }
    }
}
