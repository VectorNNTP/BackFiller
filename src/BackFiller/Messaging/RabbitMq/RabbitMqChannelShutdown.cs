using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Serilog.Debugging;

namespace VectorNNTP.BackFiller.Messaging.RabbitMq
{
    /// <summary>Closes a RabbitMQ channel and then disposes it.</summary>
    internal static class RabbitMqChannelShutdown
    {
        /// <summary>
        /// Closes <paramref name="channel"/> when it is open, then disposes it.
        /// A close that fails because the channel is already gone, or because close times out, is logged and disposal still runs.
        /// </summary>
        /// <param name="channel">Channel owned by the caller.</param>
        /// <returns>A task that completes after disposal.</returns>
        internal static async ValueTask CloseIfOpenThenDisposeAsync(IChannel channel)
        {
            ArgumentNullException.ThrowIfNull(channel);
            try
            {
                if (channel.IsOpen)
                {
                    await channel.CloseAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException
                                           or IOException or ObjectDisposedException or TimeoutException)
            {
                SelfLog.WriteLine("RabbitMQ channel close failed and disposal will continue: {0}", ex);
            }
            finally
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
