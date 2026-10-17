using VectorNNTPD.BackFillerService.Cloudflare;
using VectorNNTPD.BackFillerService.Networking;

namespace VectorNNTPD.BackFillerService.Tests.TestDoubles
{
    /// <summary>
    /// <see cref="ICloudflareDnsReconciler"/> that completes without Cloudflare API calls.
    /// Used when host composition tests require the service registration but must not perform DNS I/O.
    /// </summary>
    internal sealed class NoOpCloudflareDnsReconciler : ICloudflareDnsReconciler
    {
        /// <inheritdoc />
        public Task ReconcileAsync(
            string zoneId,
            string fqdn,
            ResolvedBindAddresses desired,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        /// <inheritdoc />
        public Task RemoveAllRecordsForFqdnAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null) =>
            Task.CompletedTask;
    }
}
