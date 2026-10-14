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
            return 0;
        }
        // codeql[cs/catch-of-all-exceptions] The process boundary must log every host failure and still return the documented exit code.
        catch (Exception ex)
        {
            return BackFillerProcessBoundary.ObserveHostFailure(ex);
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
            BackFillerLoggingExtensions.RestoreConsoleOutput();
        }
    }
}

/// <summary>
/// Final process boundary for host failures that escape <see cref="Program"/>.
/// </summary>
/// <remarks>
/// Every exception, including NNTP database authentication and availability failures, ACME and Cloudflare
/// failures, configuration validation, and cancellation, is logged and becomes exit code
/// <see cref="FailureExitCode"/>. A failure while writing that log is reported through <see cref="SelfLog"/>
/// and still returns <see cref="FailureExitCode"/>.
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
}
