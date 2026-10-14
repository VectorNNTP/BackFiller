using Serilog;
using Serilog.Debugging;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Logging;
using VectorNNTP.BackFiller.Configuration;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        var loggingCommandLine = BackFillerLoggingCommandLine.FromArguments(args);
        if (loggingCommandLine.Console)
        {
            BackFillerLoggingExtensions.UseAutoFlushConsoleOutput();
        }

        Log.Logger = BackFillerLoggingExtensions.CreateBootstrapLogger(loggingCommandLine);

        var exitCode = 0;
        try
        {
            Log.Information("{Application} host starting", ApplicationJsonConfiguration.EntryAssemblyName);

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Environment.ContentRootPath = AppContext.BaseDirectory;
            ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(
                builder.Configuration,
                builder.Environment.EnvironmentName);
            ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(
                builder.Configuration,
                builder.Environment.EnvironmentName);

            builder.ConfigureBackFillerLogging(commandLine: loggingCommandLine);
            builder.ConfigureBackFillerPlatformHosting();
            builder.AddBackFillerHosting();

            var host = builder.Build();
            BackFillerLoggingExtensions.WriteLoggingInitialized(
                host.Services,
                builder.Environment.EnvironmentName,
                builder.Environment.ContentRootPath);
            await host.RunAsync().ConfigureAwait(false);

            Log.Information("{Application} host stopped cleanly", ApplicationJsonConfiguration.EntryAssemblyName);
        }
        // codeql[cs/catch-of-all-exceptions] The process boundary must log every host failure and still return the documented exit code.
        catch (Exception ex)
        {
            exitCode = BackFillerProcessBoundary.ObserveHostFailure(ex);
        }
        finally
        {
            exitCode = await BackFillerProcessBoundary.FinishAsync(exitCode).ConfigureAwait(false);
        }

        return exitCode;
    }
}

/// <summary>
/// Final process boundary for host failures that escape <see cref="Program"/>.
/// </summary>
/// <remarks>
/// Every exception, including NNTP database authentication and availability failures, ACME and Cloudflare
/// failures, configuration validation, and cancellation, is logged and becomes exit code
/// <see cref="FailureExitCode"/>. A failure while writing that log is reported through <see cref="SelfLog"/>
/// and still returns <see cref="FailureExitCode"/>. Logger shutdown runs after that result is chosen.
/// A shutdown failure replaces a success code with <see cref="FailureExitCode"/> and is only written to
/// <see cref="SelfLog"/> when the host already failed. Console restoration still runs after either outcome.
/// </remarks>
internal static class BackFillerProcessBoundary
{
    /// <summary>Exit code returned after a host failure.</summary>
    internal const int FailureExitCode = 1;

    /// <summary>
    /// Logs <paramref name="exception"/> as a fatal host failure and returns <see cref="FailureExitCode"/>.
    /// </summary>
    /// <param name="exception">Failure that escaped the host run.</param>
    /// <returns><see cref="FailureExitCode"/>.</returns>
    internal static int ObserveHostFailure(Exception exception) =>
        ObserveHostFailure(
            exception,
            static failure => Log.Fatal(
                failure,
                "{Application} host terminated unexpectedly",
                ApplicationJsonConfiguration.EntryAssemblyName));

    /// <summary>
    /// Passes <paramref name="exception"/> to <paramref name="logFatal"/> and returns <see cref="FailureExitCode"/>.
    /// </summary>
    /// <param name="exception">Failure that escaped the host run.</param>
    /// <param name="logFatal">Fatal log writer. A throw from this callback is reported through <see cref="SelfLog"/>.</param>
    /// <returns><see cref="FailureExitCode"/>.</returns>
    internal static int ObserveHostFailure(Exception exception, Action<Exception> logFatal)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(logFatal);
        try
        {
            logFatal(exception);
        }
        // codeql[cs/catch-of-all-exceptions] The process boundary must log every host failure and still return the documented exit code.
        catch (Exception logFailure)
        {
            try
            {
                SelfLog.WriteLine("BackFiller host failure log failed: {0}", logFailure);
            }
            // codeql[cs/catch-of-all-exceptions] The process boundary must log every host failure and still return the documented exit code.
            catch (Exception)
            {
                // The process boundary must still return the failure exit code.
            }
        }

        return FailureExitCode;
    }

    /// <summary>
    /// Closes the bootstrap logger, then restores console output, and returns <paramref name="exitCode"/>.
    /// </summary>
    /// <param name="exitCode">Result already chosen for the host run. Zero means the host stopped cleanly.</param>
    /// <returns>
    /// <paramref name="exitCode"/> when logger shutdown succeeds. <see cref="FailureExitCode"/> when shutdown
    /// fails after a clean host run. The original failure code when shutdown fails after a host failure.
    /// </returns>
    internal static Task<int> FinishAsync(int exitCode) =>
        FinishAsync(
            exitCode,
            static () => Log.CloseAndFlushAsync().AsTask(),
            BackFillerLoggingExtensions.RestoreConsoleOutput);

    /// <summary>
    /// Runs <paramref name="closeAndFlush"/> and then <paramref name="restoreConsole"/>.
    /// </summary>
    /// <param name="exitCode">Result already chosen for the host run.</param>
    /// <param name="closeAndFlush">Logger shutdown. A throw is contained.</param>
    /// <param name="restoreConsole">Console restoration. It runs even when <paramref name="closeAndFlush"/> throws.</param>
    /// <returns>The exit code described by <see cref="FinishAsync(int)"/>.</returns>
    internal static async Task<int> FinishAsync(int exitCode, Func<Task> closeAndFlush, Action restoreConsole)
    {
        ArgumentNullException.ThrowIfNull(closeAndFlush);
        ArgumentNullException.ThrowIfNull(restoreConsole);
        try
        {
            await closeAndFlush().ConfigureAwait(false);
        }
        // codeql[cs/catch-of-all-exceptions] Logger shutdown must not replace the process result or skip console restoration.
        catch (Exception ex)
        {
            exitCode = ObserveShutdownFailure(exitCode, ex);
        }
        finally
        {
            try
            {
                restoreConsole();
            }
            // codeql[cs/catch-of-all-exceptions] Console restoration must not replace the process result.
            catch (Exception ex)
            {
                try
                {
                    SelfLog.WriteLine("BackFiller console restore failed: {0}", ex);
                }
                // codeql[cs/catch-of-all-exceptions] Console restoration must not replace the process result.
                catch (Exception)
                {
                    // The process boundary must still return the chosen exit code.
                }
            }
        }

        return exitCode;
    }

    /// <summary>
    /// Records <paramref name="exception"/> without discarding a host failure that was already observed.
    /// </summary>
    /// <param name="exitCode">Exit code chosen before logger shutdown.</param>
    /// <param name="exception">Failure from logger shutdown.</param>
    /// <returns>
    /// <see cref="FailureExitCode"/> when <paramref name="exitCode"/> is zero; otherwise <paramref name="exitCode"/>.
    /// </returns>
    private static int ObserveShutdownFailure(int exitCode, Exception exception)
    {
        if (exitCode == 0)
        {
            return ObserveHostFailure(exception);
        }

        try
        {
            SelfLog.WriteLine("BackFiller logger shutdown failed after a host failure: {0}", exception);
        }
        // codeql[cs/catch-of-all-exceptions] A secondary shutdown diagnostic must not replace the host failure result.
        catch (Exception)
        {
            // The original host failure code remains the process result.
        }

        return exitCode;
    }
}
