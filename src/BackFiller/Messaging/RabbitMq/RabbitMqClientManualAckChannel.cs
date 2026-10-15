using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VectorNNTP.BackFiller.ArticleWork;

namespace VectorNNTP.BackFiller.Messaging.RabbitMq
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
        private Func<RabbitMqManualAckDelivery, Task>? _onDelivery;

        /// <summary><c>1</c> after the first <see cref="DisposeAsync"/>.</summary>
        private int _disposed;

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
        public async Task<string> BasicConsumeAsync(
            string queue,
            ushort prefetchCount,
            Func<RabbitMqManualAckDelivery, Task> onDelivery,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queue);
            ArgumentNullException.ThrowIfNull(onDelivery);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (!_channel.IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ channel is not open for consume.");
            }

            _onDelivery = onDelivery;
            await _channel.BasicQosAsync(0, prefetchCount, global: false, cancellationToken).ConfigureAwait(false);
            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += OnReceivedAsync;
            _consumer = consumer;
            return await _channel
                .BasicConsumeAsync(queue, autoAck: false, consumer, cancellationToken)
                .ConfigureAwait(false);
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
        /// The disposed flag is published under <see cref="ArticleWorkSettlementLease"/>'s channel gate, after any
        /// ACK, NACK, or Basic.Cancel already inside that gate has returned. The client close runs only after the gate
        /// is released. Client close waits for the consumer callback, so holding the gate across close would deadlock
        /// a callback that still needs the gate to finish settlement. A second call does not close the client channel again.
        /// </remarks>
        public async ValueTask DisposeAsync()
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

        /// <summary>
        /// Builds a <see cref="RabbitMqManualAckDelivery"/> and awaits <see cref="_onDelivery"/>. Does not ack or nack.
        /// </summary>
        /// <param name="sender">Unused.</param>
        /// <param name="eventArgs">Client delivery. The body is passed through without copying.</param>
        /// <remarks>
        /// When no handler is installed the delivery is ignored and left unsettled. Handler exceptions propagate to the client consumer.
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
