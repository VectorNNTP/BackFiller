using System.Reflection;
using RabbitMQ.Client;
using VectorNNTP.BackFiller.Messaging.RabbitMq;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.RabbitMq
{
    public sealed class RabbitMqChannelShutdownTests
    {
        [Fact]
        public async Task Close_timeout_is_logged_and_the_channel_is_still_disposed()
        {
            var channel = ChannelDouble.Create(new TimeoutException("close timed out"));

            var diagnostic = await TestSelfLog.CaptureAsync(() =>
                RabbitMqChannelShutdown.CloseIfOpenThenDisposeAsync(channel.Channel).AsTask());

            Assert.True(channel.Disposed);
            Assert.Contains("close timed out", diagnostic, StringComparison.Ordinal);
            Assert.Contains("disposal will continue", diagnostic, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Unexpected_close_failure_still_disposes_then_propagates()
        {
            var channel = ChannelDouble.Create(new NotSupportedException("close rejected"));

            var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
                RabbitMqChannelShutdown.CloseIfOpenThenDisposeAsync(channel.Channel).AsTask());

            Assert.Equal("close rejected", ex.Message);
            Assert.True(channel.Disposed);
        }

        private class ChannelDouble : DispatchProxy
        {
            private Exception? _closeException;

            public bool Disposed { get; private set; }

            public IChannel Channel { get; private set; } = null!;

            public static ChannelDouble Create(Exception closeException)
            {
                IChannel channel = DispatchProxy.Create<IChannel, ChannelDouble>();
                var proxy = AsProxy(channel);
                proxy.Channel = channel;
                proxy._closeException = closeException;
                return proxy;
            }

            private static ChannelDouble AsProxy(IChannel channel) => (ChannelDouble)channel;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                ArgumentNullException.ThrowIfNull(targetMethod);
                if (targetMethod.Name == "get_IsOpen")
                {
                    return true;
                }

                if (targetMethod.Name == "CloseAsync")
                {
                    return Task.FromException(_closeException!);
                }

                if (targetMethod.Name == "DisposeAsync")
                {
                    Disposed = true;
                    return ValueTask.CompletedTask;
                }

                if (targetMethod.ReturnType == typeof(ValueTask))
                {
                    return ValueTask.CompletedTask;
                }

                if (typeof(Task).IsAssignableFrom(targetMethod.ReturnType))
                {
                    return Task.CompletedTask;
                }

                return targetMethod.ReturnType.IsValueType
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }
    }
}
