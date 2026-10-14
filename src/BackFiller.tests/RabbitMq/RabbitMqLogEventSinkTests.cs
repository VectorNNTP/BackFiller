using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Serilog.Parsing;
using VectorNNTP.BackFiller.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.RabbitMq
{
    public sealed class RabbitMqLogEventSinkTests
    {
        [Fact]
        public async Task Unexpected_formatter_failure_stays_inside_the_sink()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = RabbitMqServiceTests.CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            try
            {
                factory.LastConnection!.DefaultPublishConfirmBehavior = FakePublishConfirmBehavior.Confirm;
                using var sink = new RabbitMqLogEventSink(
                    service,
                    "logs",
                    "backfiller",
                    "VectorNNTP.BackFiller",
                    new ThrowingFormatter(),
                    RabbitMqLogEventSink.TextContentType);

                var diagnostic = TestSelfLog.Capture(() => sink.Emit(Event()));

                Assert.Contains("publication failed", diagnostic, StringComparison.Ordinal);
                Assert.Contains("format boom", diagnostic, StringComparison.Ordinal);
            }
            finally
            {
                await service.DisposeAsync();
            }
        }

        [Fact]
        public async Task Unexpected_channel_disposal_failure_stays_inside_the_sink()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = RabbitMqServiceTests.CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            try
            {
                var connection = factory.LastConnection!;
                connection.DefaultPublishConfirmBehavior = FakePublishConfirmBehavior.Confirm;
                connection.OnPublishChannelCreated = channel =>
                    channel.DisposeException = new NotSupportedException("channel dispose boom");
                var sink = new RabbitMqLogEventSink(
                    service,
                    "logs",
                    "backfiller",
                    "VectorNNTP.BackFiller",
                    new MessageTemplateTextFormatter("{Message}"),
                    RabbitMqLogEventSink.TextContentType);
                sink.Emit(Event());

                var diagnostic = TestSelfLog.Capture(sink.Dispose);

                Assert.Contains("failed to dispose a publish channel", diagnostic, StringComparison.Ordinal);
                Assert.Contains("channel dispose boom", diagnostic, StringComparison.Ordinal);
            }
            finally
            {
                await service.DisposeAsync();
            }
        }

        private static LogEvent Event() =>
            new(
                DateTimeOffset.UtcNow,
                LogEventLevel.Information,
                exception: null,
                new MessageTemplateParser().Parse("marker"),
                []);

        private sealed class ThrowingFormatter : ITextFormatter
        {
            public void Format(LogEvent logEvent, TextWriter output) =>
                throw new FormatException("format boom");
        }
    }
}
