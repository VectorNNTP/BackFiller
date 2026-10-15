using System.Reflection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VectorNNTP.BackFiller.Messaging.RabbitMq;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.RabbitMq
{
    public sealed class RabbitMqClientManualAckChannelTests
    {
        private static readonly TimeSpan SafetyBound = TimeSpan.FromSeconds(2);

        [Fact]
        public async Task Setup_that_wins_the_gate_publishes_the_consumer_before_disposal_closes()
        {
            var (channel, broker) = Create();
            var deliveries = 0;
            var consume = channel.BasicConsumeAsync("work", 10, CountDelivery, CancellationToken.None);

            await broker.Entered.Task.WaitAsync(SafetyBound);
            Assert.Equal(["registered"], broker.Snapshot());
            Assert.Equal(0, broker.CloseCalls);

            var dispose = channel.DisposeAsync();
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => channel.LifecycleWaiters >= 1, SafetyBound);
            Assert.Equal(0, broker.CloseCalls);
            Assert.False(dispose.IsCompleted);

            await DeliverAsync(broker.Captured!);
            Assert.Equal(1, Volatile.Read(ref deliveries));
            Assert.Equal(0, broker.CloseCalls);

            broker.Release.TrySetResult();
            Assert.Equal("ctag-1", await consume.WaitAsync(SafetyBound));
            await dispose;

            Assert.Equal(["registered", "consume-returning", "close", "disposed"], broker.Snapshot());
            Assert.Empty(broker.Registered);
            Assert.False(channel.IsOpen);
            await DeliverAsync(broker.Captured!);
            Assert.Equal(1, Volatile.Read(ref deliveries));

            Task CountDelivery(RabbitMqManualAckDelivery _)
            {
                Interlocked.Increment(ref deliveries);
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task Disposal_that_wins_the_gate_rejects_consume_before_registration()
        {
            var (channel, broker) = Create();
            broker.HoldClose = true;
            var dispose = channel.DisposeAsync();
            await broker.CloseEntered.Task.WaitAsync(SafetyBound);

            var consume = channel.BasicConsumeAsync("work", 10, static _ => Task.CompletedTask, CancellationToken.None);
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => channel.LifecycleWaiters >= 1, SafetyBound);

            Assert.Equal(0, broker.ConsumeCalls);
            Assert.False(consume.IsCompleted);
            Assert.False(channel.IsOpen);

            broker.CloseRelease.TrySetResult();
            await dispose;
            var rejected = await Assert.ThrowsAsync<ObjectDisposedException>(() => consume);
            Assert.Equal(typeof(RabbitMqClientManualAckChannel).FullName, rejected.ObjectName);
            Assert.Equal(0, broker.ConsumeCalls);
            Assert.Equal(["close", "disposed"], broker.Snapshot());
        }

        [Fact]
        public async Task A_failed_registration_detaches_the_handler_and_does_not_publish_a_consumer()
        {
            var (channel, broker) = Create();
            broker.ConsumeFailure = new InvalidOperationException("consume rejected");
            var deliveries = 0;

            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                channel.BasicConsumeAsync("work", 10, CountDelivery, CancellationToken.None));

            Assert.Equal("consume rejected", rejected.Message);
            Assert.Equal(["consume-failed"], broker.Snapshot());
            Assert.Empty(broker.Registered);
            Assert.Equal(0, broker.CloseCalls);
            await DeliverAsync(broker.Captured!);
            Assert.Equal(0, Volatile.Read(ref deliveries));

            broker.ConsumeFailure = null;
            broker.Release.TrySetResult();
            Assert.Equal("ctag-1", await channel.BasicConsumeAsync("work", 10, static _ => Task.CompletedTask, CancellationToken.None));
            Assert.Single(broker.Registered);
            Assert.Equal(0, broker.CloseCalls);

            Task CountDelivery(RabbitMqManualAckDelivery _)
            {
                Interlocked.Increment(ref deliveries);
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task Cancellation_during_registration_detaches_the_handler_and_leaves_no_consumer()
        {
            var (channel, broker) = Create();
            broker.CancelDuringConsume = true;
            using var cancellation = new CancellationTokenSource();
            var deliveries = 0;
            var consume = channel.BasicConsumeAsync("work", 10, CountDelivery, cancellation.Token);

            await broker.Entered.Task.WaitAsync(SafetyBound);
            Assert.Empty(broker.Registered);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consume);
            await DeliverAsync(broker.Captured!);
            Assert.Equal(0, Volatile.Read(ref deliveries));
            Assert.Empty(broker.Registered);
            Assert.Equal(0, broker.CloseCalls);

            broker.CancelDuringConsume = false;
            broker.Release.TrySetResult();
            Assert.Equal("ctag-1", await channel.BasicConsumeAsync("work", 10, static _ => Task.CompletedTask, CancellationToken.None));
            Assert.Single(broker.Registered);

            Task CountDelivery(RabbitMqManualAckDelivery _)
            {
                Interlocked.Increment(ref deliveries);
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task A_second_consume_waits_and_then_rejects_without_registering_another_consumer()
        {
            var (channel, broker) = Create();
            var first = channel.BasicConsumeAsync("work", 10, static _ => Task.CompletedTask, CancellationToken.None);
            await broker.Entered.Task.WaitAsync(SafetyBound);

            var second = channel.BasicConsumeAsync("work", 10, static _ => Task.CompletedTask, CancellationToken.None);
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => channel.LifecycleWaiters >= 1, SafetyBound);

            Assert.Equal(1, broker.ConsumeCalls);
            Assert.False(second.IsCompleted);

            broker.Release.TrySetResult();
            Assert.Equal("ctag-1", await first.WaitAsync(SafetyBound));
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
            Assert.Equal("RabbitMQ manual-ack channel already has a consumer.", rejected.Message);
            Assert.Equal(1, broker.ConsumeCalls);
            Assert.Single(broker.Registered);
        }

        [Fact]
        public async Task Concurrent_disposal_closes_the_client_channel_once()
        {
            var (channel, broker) = Create();
            broker.HoldClose = true;
            var first = channel.DisposeAsync();
            await broker.CloseEntered.Task.WaitAsync(SafetyBound);

            var second = channel.DisposeAsync();
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => channel.LifecycleWaiters >= 1, SafetyBound);

            Assert.Equal(1, broker.CloseCalls);
            Assert.Equal(0, broker.DisposeCalls);
            Assert.False(second.IsCompleted);

            broker.CloseRelease.TrySetResult();
            await first;
            await second;

            Assert.Equal(1, broker.CloseCalls);
            Assert.Equal(1, broker.DisposeCalls);
            Assert.False(channel.IsOpen);
            Assert.Equal(["close", "disposed"], broker.Snapshot());
        }

        [Fact]
        public async Task A_closed_channel_is_rejected_before_consumer_registration()
        {
            var (channel, broker) = Create();
            broker.IsOpen = false;

            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                channel.BasicConsumeAsync("work", 10, static _ => Task.CompletedTask, CancellationToken.None));

            Assert.Equal("RabbitMQ channel is not open for consume.", rejected.Message);
            Assert.Equal(0, broker.ConsumeCalls);
            Assert.Empty(broker.Snapshot());
        }

        private static (RabbitMqClientManualAckChannel Channel, ConsumeChannelDouble Broker) Create()
        {
            var broker = ConsumeChannelDouble.Create();
            return (new RabbitMqClientManualAckChannel(broker.Channel, generation: 3), broker);
        }

        private static Task DeliverAsync(IAsyncBasicConsumer consumer)
        {
            var eventing = Assert.IsType<AsyncEventingBasicConsumer>(consumer);
            return eventing.HandleBasicDeliverAsync(
                "ctag-1",
                deliveryTag: 11,
                redelivered: false,
                exchange: "ex",
                routingKey: "rk",
                new BasicProperties(),
                ReadOnlyMemory<byte>.Empty);
        }

        private class ConsumeChannelDouble : DispatchProxy
        {
            private readonly object _gate = new();
            private readonly List<string> _events = [];
            private readonly List<IAsyncBasicConsumer> _registered = [];

            public IChannel Channel { get; private set; } = null!;

            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource CloseRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool HoldClose { get; set; }

            public bool CancelDuringConsume { get; set; }

            public Exception? ConsumeFailure { get; set; }

            public IAsyncBasicConsumer? Captured { get; private set; }

            public int ConsumeCalls { get; private set; }

            public int CloseCalls { get; private set; }

            public int DisposeCalls { get; private set; }

            public bool IsOpen { get; set; } = true;

            public IReadOnlyList<IAsyncBasicConsumer> Registered
            {
                get
                {
                    lock (_gate)
                    {
                        return _registered.ToArray();
                    }
                }
            }

            public static ConsumeChannelDouble Create()
            {
                var channel = DispatchProxy.Create<IChannel, ConsumeChannelDouble>();
                var proxy = (ConsumeChannelDouble)(object)channel;
                proxy.Channel = channel;
                return proxy;
            }

            public string[] Snapshot()
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                ArgumentNullException.ThrowIfNull(targetMethod);
                if (targetMethod.Name == "get_IsOpen")
                {
                    return IsOpen;
                }

                if (targetMethod.Name == "BasicQosAsync")
                {
                    return Task.CompletedTask;
                }

                if (targetMethod.Name == "BasicConsumeAsync")
                {
                    return ConsumeAsync(args ?? []);
                }

                if (targetMethod.Name == "CloseAsync")
                {
                    return CloseAsync();
                }

                if (targetMethod.Name == "DisposeAsync")
                {
                    return DisposeCore();
                }

                throw new NotSupportedException(targetMethod.Name);
            }

            private async Task<string> ConsumeAsync(object?[] args)
            {
                var consumer = args[6] as IAsyncBasicConsumer;
                Assert.NotNull(consumer);
                var cancellationToken = Assert.IsType<CancellationToken>(args[7]);
                lock (_gate)
                {
                    ConsumeCalls++;
                    Captured = consumer;
                }

                if (ConsumeFailure is not null)
                {
                    Add("consume-failed");
                    throw ConsumeFailure;
                }

                if (CancelDuringConsume)
                {
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    _registered.Add(consumer);
                    _events.Add("registered");
                }

                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
                Add("consume-returning");
                return "ctag-1";
            }

            private async Task CloseAsync()
            {
                lock (_gate)
                {
                    CloseCalls++;
                    IsOpen = false;
                    _registered.Clear();
                    _events.Add("close");
                }

                if (HoldClose)
                {
                    CloseEntered.TrySetResult();
                    await CloseRelease.Task.ConfigureAwait(false);
                }
            }

            private ValueTask DisposeCore()
            {
                lock (_gate)
                {
                    DisposeCalls++;
                    _events.Add("disposed");
                }

                return ValueTask.CompletedTask;
            }

            private void Add(string name)
            {
                lock (_gate)
                {
                    _events.Add(name);
                }
            }
        }
    }
}
