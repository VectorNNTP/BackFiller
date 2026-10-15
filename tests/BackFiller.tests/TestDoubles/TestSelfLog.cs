using Serilog.Debugging;

namespace VectorNNTP.BackFiller.Tests.TestDoubles
{
    internal static class TestSelfLog
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);

        public static string Capture(Action action)
        {
            Gate.Wait();
            try
            {
                return CaptureCore(() =>
                {
                    action();
                    return Task.CompletedTask;
                }).GetAwaiter().GetResult();
            }
            finally
            {
                Gate.Release();
            }
        }

        public static async Task<string> CaptureAsync(Func<Task> action)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await CaptureCore(action).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<string> CaptureCore(Func<Task> action)
        {
            var buffer = new StringBuilder();
            SelfLog.Enable(message =>
            {
                lock (buffer)
                {
                    buffer.AppendLine(message);
                }
            });
            try
            {
                await action().ConfigureAwait(false);
                lock (buffer)
                {
                    return buffer.ToString();
                }
            }
            finally
            {
                SelfLog.Disable();
            }
        }
    }
}
