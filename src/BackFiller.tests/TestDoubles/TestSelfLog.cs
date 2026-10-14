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
                using var writer = new StringWriter();
                SelfLog.Enable(writer);
                try
                {
                    action();
                    return writer.ToString();
                }
                finally
                {
                    SelfLog.Disable();
                }
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
                using var writer = new StringWriter();
                SelfLog.Enable(writer);
                try
                {
                    await action().ConfigureAwait(false);
                    return writer.ToString();
                }
                finally
                {
                    SelfLog.Disable();
                }
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}
