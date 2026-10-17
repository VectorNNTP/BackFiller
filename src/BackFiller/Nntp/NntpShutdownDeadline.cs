using System.Collections.Concurrent;
using System.Diagnostics;

namespace VectorNNTP.BackFiller.Nntp
{
    /// <summary>
    /// Absolute shutdown instant measured with <see cref="Stopwatch.GetTimestamp"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="None"/> is not a deadline. A direct session dispose and a single-session retirement outside a pool drain use it,
    /// and those operations keep the session's own QUIT allowance. A replacement drain may pass <see cref="None"/> through its lease wait;
    /// the pool then captures one <see cref="FromBudget"/> instant for every remaining session. That instant is not reset per session.
    /// Later sessions and providers call <see cref="Remaining"/> and <see cref="Cap"/>. They do not start a new budget.
    /// The clock is monotonic. It does not follow wall-clock changes during shutdown.
    /// </remarks>
    internal readonly struct NntpShutdownDeadline
    {
        /// <summary>No shared deadline. Callers use their own operation timeout.</summary>
        public static NntpShutdownDeadline None => default;

        /// <summary><see cref="Stopwatch"/> timestamp at which this deadline is due. Meaningful only when <see cref="IsBounded"/> is set.</summary>
        private readonly long _dueTimestamp;

        /// <summary><see langword="true"/> when <see cref="FromBudget"/> created this value.</summary>
        private readonly bool _bounded;

        /// <summary>Creates a deadline that is due at <paramref name="dueTimestamp"/>.</summary>
        /// <param name="dueTimestamp">Absolute <see cref="Stopwatch.GetTimestamp"/> value.</param>
        private NntpShutdownDeadline(long dueTimestamp)
        {
            _dueTimestamp = dueTimestamp;
            _bounded = true;
        }

        /// <summary><see langword="true"/> when <see cref="Remaining"/> is finite.</summary>
        public bool IsBounded => _bounded;

        /// <summary>
        /// <see langword="true"/> when this deadline was created and its instant has been reached.
        /// <see cref="None"/> is never expired.
        /// </summary>
        public bool IsExpired => _bounded && Stopwatch.GetTimestamp() >= _dueTimestamp;

        /// <summary>
        /// Time left until <see cref="_dueTimestamp"/>, or <see cref="Timeout.InfiniteTimeSpan"/> when this value is <see cref="None"/>.
        /// </summary>
        /// <remarks>A due or past deadline returns <see cref="TimeSpan.Zero"/>. The result is never negative.</remarks>
        public TimeSpan Remaining
        {
            get
            {
                if (!_bounded)
                {
                    return Timeout.InfiniteTimeSpan;
                }

                var now = Stopwatch.GetTimestamp();
                if (now >= _dueTimestamp)
                {
                    return TimeSpan.Zero;
                }

                return Stopwatch.GetElapsedTime(now, _dueTimestamp);
            }
        }

        /// <summary>Captures a deadline <paramref name="budget"/> from the current monotonic timestamp.</summary>
        /// <param name="budget">
        /// How long the shutdown operation may run. Zero or negative is already expired, so graceful QUIT is skipped.
        /// </param>
        /// <returns>The deadline used for every later remaining-time check in that operation.</returns>
        /// <remarks>
        /// The instant is <see cref="Stopwatch.GetTimestamp"/> plus the budget converted with <see cref="Stopwatch.Frequency"/>.
        /// A later call does not extend a deadline that was already created.
        /// </remarks>
        public static NntpShutdownDeadline FromBudget(TimeSpan budget)
        {
            var now = Stopwatch.GetTimestamp();
            if (budget <= TimeSpan.Zero)
            {
                return new NntpShutdownDeadline(now);
            }

            var delta = TimestampDelta(budget);
            if (delta > long.MaxValue - now)
            {
                return new NntpShutdownDeadline(long.MaxValue);
            }

            return new NntpShutdownDeadline(now + delta);
        }

        /// <summary>Returns the shorter of <paramref name="allowance"/> and <see cref="Remaining"/>.</summary>
        /// <param name="allowance">Operation cap used when this deadline is <see cref="None"/> or has more time left.</param>
        /// <returns>
        /// <paramref name="allowance"/> when this deadline is unbounded or has at least that much time left.
        /// Otherwise the time remaining, which is <see cref="TimeSpan.Zero"/> after the deadline.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="allowance"/> is negative.</exception>
        public TimeSpan Cap(TimeSpan allowance)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(allowance, TimeSpan.Zero);
            if (!_bounded)
            {
                return allowance;
            }

            var remaining = Remaining;
            return remaining < allowance ? remaining : allowance;
        }

        /// <summary>Converts <paramref name="budget"/> into a <see cref="Stopwatch"/> timestamp delta.</summary>
        /// <param name="budget">Positive duration. The caller rejects zero and negative budgets.</param>
        /// <returns>A positive timestamp delta. Very large budgets are clamped so the addition cannot overflow.</returns>
        private static long TimestampDelta(TimeSpan budget)
        {
            var frequency = Stopwatch.Frequency;
            if (budget.Ticks >= long.MaxValue / frequency)
            {
                return long.MaxValue / 4;
            }

            var delta = budget.Ticks * frequency / TimeSpan.TicksPerSecond;
            return delta <= 0 ? 1 : delta;
        }
    }

    /// <summary>
    /// Runs independent shutdown work on a fixed number of workers and still waits for every item.
    /// </summary>
    /// <remarks>
    /// The degree does not grow with the item count. A worker that is blocked in one item does not stop the other workers
    /// from taking later items. An item fault is stored and the worker continues. After every item has been attempted,
    /// the stored faults are thrown as one <see cref="AggregateException"/>. Process-integrity failures fault that worker
    /// immediately and are not stored.
    /// </remarks>
    internal static class NntpShutdownWork
    {
        /// <summary>
        /// Workers used to retire sessions in one pool and pools in one registry shutdown.
        /// One blocked QUIT occupies one worker until the shared deadline cancels that write.
        /// </summary>
        internal const int Parallelism = 4;

        /// <summary>Invokes <paramref name="action"/> for every item, with at most <see cref="Parallelism"/> invocations at once.</summary>
        /// <typeparam name="T">Item type. The list is not mutated.</typeparam>
        /// <param name="items">Work already snapshotted by the caller. An empty list returns without starting a worker.</param>
        /// <param name="action">One item's cleanup. It must not depend on later items in <paramref name="items"/> having finished.</param>
        /// <returns>A task that completes after every item has been attempted.</returns>
        /// <exception cref="AggregateException">One or more item actions threw an exception other than a process-integrity failure.</exception>
        /// <remarks>
        /// Process-integrity failures are not caught here. They fault the worker that observed them.
        /// <see cref="Task.WhenAll(Task[])"/> still waits for the other workers, then surfaces that failure.
        /// </remarks>
        internal static async Task RunBoundedAsync<T>(IReadOnlyList<T> items, Func<T, Task> action)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(action);
            if (items.Count == 0)
            {
                return;
            }

            var workers = Math.Min(Parallelism, items.Count);
            var next = 0;
            var faults = new ConcurrentQueue<Exception>();
            var tasks = new Task[workers];
            for (var worker = 0; worker < workers; worker++)
            {
                tasks[worker] = RunWorkerAsync();
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            if (!faults.IsEmpty)
            {
                throw new AggregateException(faults);
            }

            async Task RunWorkerAsync()
            {
                while (true)
                {
                    var index = Interlocked.Increment(ref next) - 1;
                    if ((uint)index >= (uint)items.Count)
                    {
                        return;
                    }

                    try
                    {
                        await action(items[index]).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException
                        and not StackOverflowException
                        and not AccessViolationException
                        and not AppDomainUnloadedException
                        and not BadImageFormatException
                        and not CannotUnloadAppDomainException)
                    {
                        faults.Enqueue(ex);
                    }
                }
            }
        }
    }
}
