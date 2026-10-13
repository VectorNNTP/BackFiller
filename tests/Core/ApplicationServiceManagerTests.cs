using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Core;
using BackFillerApplicationServiceManager = VectorNNTP.BackFiller.Core.ApplicationServiceManager;

namespace VectorNNTP.BackFiller.Tests.Core
{
    public sealed class ApplicationServiceManagerTests
    {
        [Fact]
        public async Task Starts_in_registration_order_and_stops_in_reverse()
        {
            var order = new List<string>();
            var first = new TrackingService("acme", order);
            var second = new TrackingService("listener", order);
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb());
            var manager = new BackFillerApplicationServiceManager(
                [first, second],
                runtime,
                NullLogger<BackFillerApplicationServiceManager>.Instance);

            await manager.StartAsync(CancellationToken.None);
            Assert.Equal(["start:acme", "start:listener"], order);
            Assert.Equal([first, second], manager.StartedServices);

            await manager.StopAsync(CancellationToken.None);
            Assert.Equal(["start:acme", "start:listener", "stop:listener", "stop:acme"], order);
            Assert.Empty(manager.StartedServices);
        }

        [Fact]
        public async Task Startup_failure_rolls_back_already_started_services()
        {
            var order = new List<string>();
            var first = new TrackingService("acme", order);
            var second = new TrackingService("listener", order, startException: new InvalidOperationException("listener blocked"));
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb());
            var manager = new BackFillerApplicationServiceManager(
                [first, second],
                runtime,
                NullLogger<BackFillerApplicationServiceManager>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(CancellationToken.None));
            Assert.Equal(["start:acme", "start:listener", "stop:acme"], order);
            Assert.Empty(manager.StartedServices);
        }

        [Fact]
        public async Task Unexpected_termination_event_for_completed_execution_has_null_exception()
        {
            var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new MonitoredService("worker", execution.Task);
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb());
            var manager = new BackFillerApplicationServiceManager(
                [service],
                runtime,
                NullLogger<BackFillerApplicationServiceManager>.Instance);

            Task<(string ServiceName, Exception? Exception)> termination = AwaitUnexpectedTerminationAsync(manager);
            await manager.StartAsync(CancellationToken.None);
            execution.SetResult();

            var observed = await termination;
            Assert.Equal("worker", observed.ServiceName);
            Assert.Null(observed.Exception);

            await manager.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task Unexpected_termination_event_for_faulted_execution_has_non_null_exception()
        {
            var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new MonitoredService("worker", execution.Task);
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb());
            var manager = new BackFillerApplicationServiceManager(
                [service],
                runtime,
                NullLogger<BackFillerApplicationServiceManager>.Instance);

            Task<(string ServiceName, Exception? Exception)> termination = AwaitUnexpectedTerminationAsync(manager);
            await manager.StartAsync(CancellationToken.None);
            execution.SetException(new InvalidOperationException("boom"));

            var observed = await termination;
            Assert.Equal("worker", observed.ServiceName);
            Assert.NotNull(observed.Exception);
            Assert.IsType<InvalidOperationException>(observed.Exception);

            await manager.StopAsync(CancellationToken.None);
        }

        private static async Task<(string ServiceName, Exception? Exception)> AwaitUnexpectedTerminationAsync(
            BackFillerApplicationServiceManager manager)
        {
            var completion = new TaskCompletionSource<(string ServiceName, Exception? Exception)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            manager.UnexpectedServiceTermination += (_, args) => completion.TrySetResult((args.ServiceName, args.Exception));

            Task completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(completion.Task, completed);
            return await completion.Task;
        }

        private sealed class MonitoredService : IApplicationService
        {
            public MonitoredService(string name, Task execution)
            {
                Name = name;
                Execution = execution;
            }

            public string Name { get; }

            public Task? Execution { get; }

            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class TrackingService : IApplicationService
        {
            private readonly List<string> _order;
            private readonly Exception? _startException;

            public TrackingService(string name, List<string> order, Exception? startException = null)
            {
                Name = name;
                _order = order;
                _startException = startException;
            }

            public string Name { get; }

            public Task? Execution => null;

            public Task StartAsync(CancellationToken cancellationToken)
            {
                _order.Add("start:" + Name);
                if (_startException is not null)
                {
                    throw _startException;
                }

                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken)
            {
                _order.Add("stop:" + Name);
                return Task.CompletedTask;
            }
        }
    }
}
