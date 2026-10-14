using System.Reflection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Sinks.File;
using Serilog.Parsing;
using VectorNNTP.BackFiller.Logging;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Logging
{
    public sealed class CodeQlLoggingRemediationTests
    {
        private static readonly object ConsoleGate = new();

        [Fact]
        public void Daily_checkpoint_crosses_29_February_and_keeps_kind()
        {
            using var sink = Create(RollingInterval.Day);
            var leap = new DateTime(2024, 2, 29, 15, 4, 5, DateTimeKind.Utc);

            Assert.Equal(new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc), sink.Checkpoint(leap));
            Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), sink.NextCheckpoint(leap));
        }

        [Fact]
        public void Monthly_checkpoint_crosses_December_and_month_end()
        {
            using var sink = Create(RollingInterval.Month);
            var end = new DateTime(2024, 12, 31, 23, 0, 0, DateTimeKind.Utc);
            var january = new DateTime(2024, 1, 31, 8, 0, 0, DateTimeKind.Unspecified);

            Assert.Equal(new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc), sink.Checkpoint(end));
            Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), sink.NextCheckpoint(end));
            Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), sink.Checkpoint(january));
            Assert.Equal(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Unspecified), sink.NextCheckpoint(january));
        }

        [Fact]
        public void Yearly_checkpoint_rejects_a_year_that_cannot_be_represented()
        {
            using var sink = Create(RollingInterval.Year);
            var last = new DateTime(9999, 6, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.Equal(new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc), sink.Checkpoint(last));
            Assert.Throws<ArgumentOutOfRangeException>(() => sink.NextCheckpoint(last));
        }

        [Fact]
        public void Hourly_checkpoint_crosses_midnight()
        {
            using var sink = Create(RollingInterval.Hour);
            var instant = new DateTime(2024, 2, 29, 23, 30, 0, DateTimeKind.Local);

            Assert.Equal(new DateTime(2024, 2, 29, 23, 0, 0, DateTimeKind.Local), sink.Checkpoint(instant));
            Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Local), sink.NextCheckpoint(instant));
        }

        [Fact]
        public void Log_file_name_cannot_leave_the_directory()
        {
            var directory = Directory.CreateTempSubdirectory("bf-roll-name-").FullName;
            try
            {
                Assert.Throws<ArgumentException>(() => Create(RollingInterval.Infinite, Path.Join(directory, "..")));
            }
            finally
            {
                Directory.Delete(directory);
            }
        }

        [Fact]
        public void Dispose_flushes_the_last_event_and_a_second_dispose_is_safe()
        {
            var directory = Directory.CreateTempSubdirectory("bf-flush-").FullName;
            try
            {
                var path = Path.Join(directory, "app-.log");
                var rolling = new GzipRollingFileSink(
                    path,
                    new MessageTemplateTextFormatter("{Message}"),
                    fileSizeLimitBytes: null,
                    retainedFileCountLimit: 2,
                    encoding: null,
                    buffered: true,
                    RollingInterval.Infinite,
                    rollOnFileSizeLimit: false);
                using var sink = new PeriodicFileFlushSink(rolling, TimeSpan.FromHours(1));
                sink.Emit(new LogEvent(
                    DateTimeOffset.UtcNow,
                    LogEventLevel.Information,
                    exception: null,
                    new MessageTemplateParser().Parse("flushed-marker"),
                    []));
                sink.Dispose();
                sink.Dispose();

                var written = File.ReadAllText(Directory.GetFiles(directory, "app*.log").Single());
                Assert.Contains("flushed-marker", written, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Console_writer_replaces_and_restores_without_closing_stdout()
        {
            lock (ConsoleGate)
            {
                var before = Console.Out;
                try
                {
                    BackFillerLoggingExtensions.UseAutoFlushConsoleOutput();
                    var installed = Console.Out;
                    Assert.NotSame(before, installed);
                    BackFillerLoggingExtensions.UseAutoFlushConsoleOutput();
                    Assert.Same(installed, Console.Out);
                    installed.Write("bf-console");
                    BackFillerLoggingExtensions.RestoreConsoleOutput();
                    Assert.Same(before, Console.Out);
                    before.Flush();
                    Console.Out.Flush();
                    using (var stdout = Console.OpenStandardOutput())
                    {
                        stdout.Write([]);
                        stdout.Flush();
                    }

                    BackFillerLoggingExtensions.RestoreConsoleOutput();
                    Assert.Same(before, Console.Out);
                }
                finally
                {
                    BackFillerLoggingExtensions.RestoreConsoleOutput();
                    if (!ReferenceEquals(Console.Out, before))
                    {
                        Console.SetOut(before);
                    }
                }
            }
        }

        [Fact]
        public async Task Flush_callback_blocks_disposal_until_the_callback_leaves_the_lock()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var inner = new BlockingFlushSink(entered, release);
            using var sink = new PeriodicFileFlushSink(inner, TimeSpan.FromHours(1));
            sink.Emit(new LogEvent(
                DateTimeOffset.UtcNow,
                LogEventLevel.Information,
                exception: null,
                new MessageTemplateParser().Parse("race"),
                []));

            var onTick = typeof(PeriodicFileFlushSink).GetMethod(
                "OnTick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onTick);
            var callback = Task.Run(() => onTick.Invoke(sink, [null]));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var disposal = Task.Run(sink.Dispose);
            Assert.False(disposal.IsCompleted);
            release.SetResult();
            await callback.WaitAsync(TimeSpan.FromSeconds(2));
            await disposal.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, inner.FlushCount);
            Assert.Equal(1, inner.DisposeCount);
        }

        [Fact]
        public void Timer_callback_contains_a_flush_failure()
        {
            var inner = new FaultingFlushSink { FlushException = new IOException("timer flush") };
            using var sink = new PeriodicFileFlushSink(inner, TimeSpan.FromHours(1));
            Emit(sink);

            var diagnostic = TestSelfLog.Capture(() => InvokeOnTick(sink));

            Assert.Contains("timer callback failed", diagnostic, StringComparison.Ordinal);
            Assert.Contains("timer flush", diagnostic, StringComparison.Ordinal);
            sink.Dispose();
            Assert.Equal(1, inner.DisposeCount);
            AssertTimerDisposed(sink);
        }

        [Fact]
        public void Final_flush_failure_still_disposes_the_sink_and_timer()
        {
            var inner = new FaultingFlushSink { FlushException = new IOException("final flush") };
            using var sink = new PeriodicFileFlushSink(inner, TimeSpan.FromHours(1));
            Emit(sink);

            var diagnostic = TestSelfLog.Capture(() =>
            {
                var ex = Assert.Throws<IOException>(sink.Dispose);
                Assert.Equal("final flush", ex.Message);
            });

            Assert.Contains("failed during disposal", diagnostic, StringComparison.Ordinal);
            Assert.Equal(1, inner.DisposeCount);
            AssertTimerDisposed(sink);
            Assert.Equal(1, inner.DisposeCount);
        }

        [Fact]
        public void Inner_sink_disposal_failure_still_disposes_the_timer()
        {
            var inner = new FaultingFlushSink { DisposeException = new InvalidOperationException("sink dispose") };
            var sink = new PeriodicFileFlushSink(inner, TimeSpan.FromHours(1));

            var ex = Assert.Throws<InvalidOperationException>(sink.Dispose);

            Assert.Equal("sink dispose", ex.Message);
            Assert.Equal(1, inner.DisposeCount);
            AssertTimerDisposed(sink);
        }

        [Fact]
        public void Flush_failure_stays_primary_when_disposal_also_fails()
        {
            var inner = new FaultingFlushSink
            {
                FlushException = new IOException("primary flush"),
                DisposeException = new NotSupportedException("secondary dispose"),
            };
            var sink = new PeriodicFileFlushSink(inner, TimeSpan.FromHours(1));
            Emit(sink);

            var diagnostic = TestSelfLog.Capture(() =>
            {
                var ex = Assert.Throws<IOException>(sink.Dispose);
                Assert.Equal("primary flush", ex.Message);
            });

            Assert.Contains("sink disposal failed", diagnostic, StringComparison.Ordinal);
            Assert.Contains("secondary dispose", diagnostic, StringComparison.Ordinal);
            Assert.Equal(1, inner.DisposeCount);
            AssertTimerDisposed(sink);
        }

        [Fact]
        public async Task Concurrent_dispose_disposes_the_inner_sink_once()
        {
            var inner = new FaultingFlushSink();
            var sink = new PeriodicFileFlushSink(inner, TimeSpan.FromHours(1));
            var first = Task.Run(sink.Dispose);
            var second = Task.Run(sink.Dispose);
            await Task.WhenAll(first, second);

            Assert.Equal(1, inner.DisposeCount);
            AssertTimerDisposed(sink);
        }

        private static void Emit(PeriodicFileFlushSink sink) =>
            sink.Emit(new LogEvent(
                DateTimeOffset.UtcNow,
                LogEventLevel.Information,
                exception: null,
                new MessageTemplateParser().Parse("marker"),
                []));

        private static void InvokeOnTick(PeriodicFileFlushSink sink)
        {
            var onTick = typeof(PeriodicFileFlushSink).GetMethod(
                "OnTick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onTick);
            onTick.Invoke(sink, [null]);
        }

        private static void AssertTimerDisposed(PeriodicFileFlushSink sink)
        {
            var timerField = typeof(PeriodicFileFlushSink).GetField(
                "_timer",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var timer = Assert.IsType<Timer>(timerField?.GetValue(sink));
            Assert.False(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        }

        private static GzipRollingFileSink Create(RollingInterval interval, string? path = null)
        {
            path ??= Path.Join(Path.GetTempPath(), "bf-checkpoint-.log");
            return new GzipRollingFileSink(
                path,
                new MessageTemplateTextFormatter("{Message}"),
                fileSizeLimitBytes: null,
                retainedFileCountLimit: 2,
                encoding: null,
                buffered: false,
                interval,
                rollOnFileSizeLimit: false);
        }

        private sealed class BlockingFlushSink : ILogEventSink, IFlushableFileSink, IDisposable
        {
            private readonly TaskCompletionSource _entered;
            private readonly TaskCompletionSource _release;
            private int _flushCount;
            private int _disposeCount;

            internal BlockingFlushSink(TaskCompletionSource entered, TaskCompletionSource release)
            {
                _entered = entered;
                _release = release;
            }

            internal int FlushCount => Volatile.Read(ref _flushCount);

            internal int DisposeCount => Volatile.Read(ref _disposeCount);

            public void Emit(LogEvent logEvent)
            {
            }

            public void FlushToDisk()
            {
                Interlocked.Increment(ref _flushCount);
                _entered.TrySetResult();
                _release.Task.GetAwaiter().GetResult();
            }

            public void Dispose()
            {
                Interlocked.Increment(ref _disposeCount);
            }
        }

        private sealed class FaultingFlushSink : ILogEventSink, IFlushableFileSink, IDisposable
        {
            private int _disposeCount;

            public Exception? FlushException { get; init; }

            public Exception? DisposeException { get; init; }

            public int DisposeCount => Volatile.Read(ref _disposeCount);

            public void Emit(LogEvent logEvent)
            {
            }

            public void FlushToDisk()
            {
                if (FlushException is not null)
                {
                    throw FlushException;
                }
            }

            public void Dispose()
            {
                Interlocked.Increment(ref _disposeCount);
                if (DisposeException is not null)
                {
                    throw DisposeException;
                }
            }
        }
    }
}
