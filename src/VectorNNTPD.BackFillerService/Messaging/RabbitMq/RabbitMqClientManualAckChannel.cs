using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VectorNNTPD.BackFillerService.ArticleWork;

namespace VectorNNTPD.BackFillerService.Messaging.RabbitMq
{
    /// <summary>Owns one RabbitMQ.Client channel for topology, manual-ack consume, and settlement.</summary>
    internal sealed class RabbitMqClientManualAckChannel : IRabbitMqManualAckChannel
    {
        /// <summary>
        /// Owned channel with publisher confirms disabled. <see cref="DisposeAsync"/> unsubscribes the consumer,
        /// closes the channel when open (close errors are swallowed), then disposes it.
        /// </summary>
        private readonly IChannel _channel;

        /// <summary>Consumer registered by <see cref="BasicConsumeAsync"/>. Unsubscribed on dispose.</summary>
        private AsyncEventingBasicConsumer? _consumer;

        /// <summary>Delivery handler from <see cref="BasicConsumeAsync"/>. A null handler drops the delivery without ack or nack.</summary>
        private volatile Func<RabbitMqManualAckDelivery, Task>? _onDelivery;

        /// <summary><c>1</c> after the first <see cref="DisposeAsync"/>.</summary>
        private int _disposed;

        /// <summary>
        /// Serializes <see cref="BasicConsumeAsync"/> with <see cref="DisposeAsync"/> across the awaited broker calls.
        /// A monitor cannot be held across those awaits. This semaphore is not disposed: a late consume still has to observe
        /// <see cref="_disposed"/> after the first disposal has released the gate. It has no named wait handle.
        /// </summary>
        private readonly SemaphoreSlim _lifecycle = new(1, 1);

        /// <summary>Callers blocked in <see cref="EnterLifecycleAsync"/> before they hold <see cref="_lifecycle"/>.</summary>
        private int _lifecycleWaiters;

        /// <summary>
        /// Initializes a new wrapper around an opened channel.
        /// </summary>
        /// <param name="channel">Opened RabbitMQ.Client channel.</param>
        /// <param name="generation">Connection generation the channel belongs to.</param>
        internal RabbitMqClientManualAckChannel(IChannel channel, long generation)
        {
            ArgumentNullException.ThrowIfNull(channel);
            _channel = channel;
            Generation = generation;
        }

        /// <inheritdoc />
        public long Generation { get; }

        /// <inheritdoc />
        /// <remarks>
        /// False after <see cref="DisposeAsync"/> has marked this wrapper, even if the client channel has not finished closing.
        /// Settlement checks this inside the channel gate, so it does not start a broker RPC once disposal has entered.
        /// </remarks>
        public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _channel.IsOpen;

        /// <summary>Gets how many consume or dispose calls are waiting to enter the lifecycle gate.</summary>
        /// <remarks>The holder is not counted. Tests use this to observe a blocked dispose or a second consume.</remarks>
        internal int LifecycleWaiters => Volatile.Read(ref _lifecycleWaiters);

        /// <inheritdoc />
        public Task ExchangeDeclareAsync(
            string exchange,
            string type,
            bool durable,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
            ArgumentException.ThrowIfNullOrWhiteSpace(type);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return _channel.ExchangeDeclareAsync(
                exchange,
                type,
                durable,
                autoDelete,
                ToMutable(arguments),
                cancellationToken: cancellationToken);
        }

        /// <inheritdoc />
        public async Task QueueDeclareAsync(
            string queue,
            bool durable,
            bool exclusive,
            bool autoDelete,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queue);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            _ = await _channel.QueueDeclareAsync(
                queue,
                durable,
                exclusive,
                autoDelete,
                ToMutable(arguments),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task QueueBindAsync(
            string queue,
            string exchange,
            string routingKey,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queue);
            ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
            ArgumentNullException.ThrowIfNull(routingKey);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return _channel.QueueBindAsync(
                queue,
                exchange,
                routingKey,
                ToMutable(arguments),
                cancellationToken: cancellationToken);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Holds <see cref="_lifecycle"/> across QoS and Basic.Consume. The handler is installed before Basic.Consume so a
        /// delivery that arrives with Consume-Ok is not dropped, and <see cref="_consumer"/> is assigned only after that call
        /// returns, before the gate is released. A second call throws <see cref="InvalidOperationException"/>.
        /// RabbitMQ.Client 7 adds the dispatcher consumer inside Consume-Ok handling and removes it if that handling throws,
        /// so a faulted <see cref="IChannel.BasicConsumeAsync"/> does not leave a client-tracked consumer. This method still
        /// detaches the local handler before releasing the gate. It does not close the channel; the caller disposes on failure.
        /// </remarks>
        public async Task<string> BasicConsumeAsync(
            string queue,
            ushort prefetchCount,
            Func<RabbitMqManualAckDelivery, Task> onDelivery,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queue);
            ArgumentNullException.ThrowIfNull(onDelivery);
            await EnterLifecycleAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                if (_consumer is not null)
                {
                    throw new InvalidOperationException("RabbitMQ manual-ack channel already has a consumer.");
                }

                if (!_channel.IsOpen)
                {
                    throw new InvalidOperationException("RabbitMQ channel is not open for consume.");
                }

                await _channel.BasicQosAsync(0, prefetchCount, global: false, cancellationToken).ConfigureAwait(false);
                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += OnReceivedAsync;
                _onDelivery = onDelivery;
                try
                {
                    var tag = await _channel
                        .BasicConsumeAsync(queue, autoAck: false, consumer, cancellationToken)
                        .ConfigureAwait(false);
                    _consumer = consumer;
                    return tag;
                }
                catch (Exception)
                {
                    // codeql[cs/catch-of-all-exceptions] A failed or cancelled registration must detach the local handler and then propagate.
                    _onDelivery = null;
                    consumer.ReceivedAsync -= OnReceivedAsync;
                    throw;
                }
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        /// <inheritdoc />
        public Task BasicCancelAsync(string consumerTag, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(consumerTag);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return _channel.BasicCancelAsync(consumerTag, cancellationToken: cancellationToken);
        }

        /// <inheritdoc />
        public Task BasicAckAsync(ulong deliveryTag, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return _channel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken).AsTask();
        }

        /// <inheritdoc />
        public Task BasicNackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return _channel.BasicNackAsync(deliveryTag, multiple: false, requeue, cancellationToken).AsTask();
        }

        /// <inheritdoc />
        /// <remarks>
        /// <see cref="_lifecycle"/> is acquired first, so an in-flight <see cref="BasicConsumeAsync"/> publishes
        /// <see cref="_consumer"/> or clears a failed registration before disposal unsubscribes or closes.
        /// The disposed flag is then published under <see cref="ArticleWorkSettlementLease"/>'s channel gate, after any
        /// ACK, NACK, or Basic.Cancel already inside that gate has returned. The client close runs only after that
        /// settlement gate is released, while <see cref="_lifecycle"/> is still held so a new consume cannot start on
        /// the closing channel. Client close waits for the consumer callback, so holding the settlement gate across
        /// close would deadlock a callback that still needs that gate to finish settlement. A second call waits for
        /// the first close to finish and does not close the client channel again.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            await EnterLifecycleAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var close = false;
                await ArticleWorkSettlementLease.RunExclusiveAsync(this, () =>
                {
                    if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    {
                        _consumer?.ReceivedAsync -= OnReceivedAsync;
                        _consumer = null;
                        _onDelivery = null;
                        close = true;
                    }

                    return Task.CompletedTask;
                }).ConfigureAwait(false);

                if (close)
                {
                    await RabbitMqChannelShutdown.CloseIfOpenThenDisposeAsync(_channel).ConfigureAwait(false);
                }
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        /// <summary>Waits for <see cref="_lifecycle"/> and counts this caller only while it is blocked.</summary>
        /// <param name="cancellationToken">Cancels consume setup before it enters. Disposal passes <see cref="CancellationToken.None"/>.</param>
        /// <returns>A task that completes when this caller holds the gate.</returns>
        /// <remarks>A cancelled wait does not hold the semaphore and must not release it.</remarks>
        private async Task EnterLifecycleAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _lifecycleWaiters);
            try
            {
                await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _lifecycleWaiters);
            }
        }

        /// <summary>
        /// Builds a <see cref="RabbitMqManualAckDelivery"/> and awaits <see cref="_onDelivery"/>. Does not ack or nack.
        /// </summary>
        /// <param name="sender">Unused.</param>
        /// <param name="eventArgs">Client delivery. The body is passed through without copying.</param>
        /// <remarks>
        /// When no handler is installed, including after disposal clears it, the delivery is ignored and left unsettled.
        /// The handler reference is read once. Handler exceptions propagate to the client consumer.
        /// </remarks>
        private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs eventArgs)
        {
            var handler = _onDelivery;
            if (handler is null)
            {
                return;
            }

            var delivery = new RabbitMqManualAckDelivery(
                eventArgs.DeliveryTag,
                eventArgs.Body,
                eventArgs.BasicProperties.CorrelationId,
                eventArgs.BasicProperties.ReplyTo,
                eventArgs.BasicProperties.ContentType,
                ReadRequestId(eventArgs.BasicProperties.Headers),
                eventArgs.Redelivered,
                eventArgs.RoutingKey,
                eventArgs.Exchange,
                eventArgs.ConsumerTag,
                Generation);
            await handler(delivery).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads the <c>RequestId</c> header as a string, UTF-8 <see cref="byte"/> array, or <see cref="ReadOnlyMemory{T}"/> of bytes.
        /// </summary>
        /// <param name="headers">AMQP headers. <see langword="null"/>, a missing key, or any other value type yields <see langword="null"/>.</param>
        /// <returns>The request id text, or <see langword="null"/> when it is absent or not one of those encodings.</returns>
        private static string? ReadRequestId(IDictionary<string, object?>? headers)
        {
            if (headers is null
                || !headers.TryGetValue("RequestId", out var value)
                || value is null)
            {
                return null;
            }

            return value switch
            {
                string text => text,
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
                _ => null,
            };
        }

        /// <summary>Copies <paramref name="arguments"/> into a new ordinal dictionary for RabbitMQ.Client.</summary>
        /// <param name="arguments">Declare or bind arguments. Not mutated.</param>
        /// <returns><see langword="null"/> when <paramref name="arguments"/> is <see langword="null"/>; otherwise a new dictionary.</returns>
        private static Dictionary<string, object?>? ToMutable(IReadOnlyDictionary<string, object?>? arguments)
        {
            if (arguments is null)
            {
                return null;
            }

            return new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
        }
    }
}
