namespace VectorNNTPD.BackFillerService.Hosting.Systemd
{
    /// <summary>
    /// Calculates systemd watchdog heartbeat intervals from the systemd-provided deadline.
    /// </summary>
    internal static class SystemdWatchdogInterval
    {
        /// <summary>
        /// Shortest deadline that can still be serviced. The heartbeat is at least one millisecond and
        /// at least one millisecond earlier than the deadline, so a shorter deadline is rejected.
        /// </summary>
        internal static readonly TimeSpan MinimumTimeout = TimeSpan.FromMilliseconds(2);

        /// <summary>
        /// Derives a heartbeat interval from a fraction of the systemd watchdog deadline.
        /// </summary>
        /// <param name="watchdogTimeout">Deadline supplied by systemd (<c>WATCHDOG_USEC</c>).</param>
        /// <param name="fraction">Fraction of the deadline between heartbeats; must be in (0, 1).</param>
        /// <returns>
        /// The floored fraction of <paramref name="watchdogTimeout"/>, raised to at least one millisecond
        /// and limited so it remains at least one millisecond before the deadline.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="watchdogTimeout"/> is below <see cref="MinimumTimeout"/>,
        /// or when <paramref name="fraction"/> is NaN or outside the open interval (0, 1).
        /// </exception>
        internal static TimeSpan Calculate(TimeSpan watchdogTimeout, double fraction = 0.5)
        {
            if (watchdogTimeout < MinimumTimeout)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(watchdogTimeout),
                    watchdogTimeout,
                    "Watchdog timeout must be at least 2 milliseconds so the heartbeat stays at least 1 millisecond before the deadline.");
            }

            if (double.IsNaN(fraction) || fraction is <= 0 or >= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "Fraction must be in the open interval (0, 1).");
            }

            var ticks = (long)Math.Floor(watchdogTimeout.Ticks * fraction);
            if (ticks < TimeSpan.TicksPerMillisecond)
            {
                ticks = TimeSpan.TicksPerMillisecond;
            }

            var latest = watchdogTimeout.Ticks - TimeSpan.TicksPerMillisecond;
            if (ticks > latest)
            {
                ticks = latest;
            }

            return TimeSpan.FromTicks(ticks);
        }
    }
}
