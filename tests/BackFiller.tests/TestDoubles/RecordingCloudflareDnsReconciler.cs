using VectorNNTP.BackFiller.Cloudflare;
using VectorNNTP.BackFiller.Networking;

namespace VectorNNTP.BackFiller.Tests.TestDoubles
{
    /// <summary>
    /// Records Cloudflare reconcile/remove calls for lifecycle assertions.
    /// </summary>

    internal sealed class RecordingCloudflareDnsReconciler : ICloudflareDnsReconciler
    {
        /// <summary>Serializes appends to recorded call lists from concurrent reconcile/remove invocations.</summary>
        private readonly object _gate = new();

        /// <summary>Recorded <see cref="ReconcileAsync"/> zone/FQDN pairs; no Cloudflare API calls.</summary>
        public List<(string ZoneId, string Fqdn)> ReconcileCalls { get; } = [];

        /// <summary>Recorded <see cref="RemoveAllRecordsForFqdnAsync"/> zone/FQDN pairs.</summary>
        public List<(string ZoneId, string Fqdn)> RemoveCalls { get; } = [];

        /// <summary>When set, thrown after the reconcile call is recorded; does not contact Cloudflare.</summary>
        public Exception? ReconcileException { get; set; }

        /// <summary>
        /// Records zone and FQDN under lock, then throws <see cref="ReconcileException"/> when configured.
        /// Ignores bind addresses beyond null validation; no DNS mutation occurs.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone identifier passed through for assertions.</param>
        /// <param name="fqdn">Fully qualified name passed through for assertions.</param>
        /// <param name="desired">Resolved bind addresses required by the interface; not applied in the fake.</param>
        /// <param name="cancellationToken">Honored before recording or throwing.</param>
        public Task ReconcileAsync(
            string zoneId,
            string fqdn,
            ResolvedBindAddresses desired,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            ArgumentNullException.ThrowIfNull(desired);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                ReconcileCalls.Add((zoneId, fqdn));
            }

            if (ReconcileException is not null)
            {
                throw ReconcileException;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Appends zone and FQDN to <see cref="RemoveCalls"/> under lock.
        /// Does not delete records or honor <paramref name="operationTimeout"/>.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone identifier passed through for assertions.</param>
        /// <param name="fqdn">Fully qualified name passed through for assertions.</param>
        /// <param name="cancellationToken">Honored before recording.</param>
        /// <param name="operationTimeout">Ignored; present only for interface compatibility.</param>
        public Task RemoveAllRecordsForFqdnAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                RemoveCalls.Add((zoneId, fqdn));
            }

            return Task.CompletedTask;
        }
    }
}
