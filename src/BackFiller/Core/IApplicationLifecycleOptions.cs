namespace VectorNNTP.BackFiller.Core
{
    /// <summary>
    /// Lifecycle and graceful-shutdown knobs shared by hosts that use
    /// <see cref="ApplicationLifecycle"/> / <see cref="VectorNNTP.BackFiller.Core.ApplicationServiceManager"/>.
    /// </summary>
    public interface IApplicationLifecycleOptions
    {
        /// <summary>Gets the application display name used in lifecycle logs.</summary>
        string ApplicationName { get; }

        /// <summary>Gets the overall graceful shutdown wall-clock budget.</summary>
        TimeSpan GracefulShutdownTimeout { get; }

        /// <summary>
        /// Gets the optional startup timeout. When <see langword="null"/>, startup is bounded
        /// only by the caller's cancellation token.
        /// </summary>
        TimeSpan? StartupTimeout { get; }

        /// <summary>
        /// Gets a value indicating whether the process should exit if an application service
        /// terminates unexpectedly while running.
        /// </summary>
        /// <remarks>
        /// Shared contract with NNTPD. BackFiller binds this option but does not read it:
        /// <see cref="VectorNNTP.BackFiller.Hosting.BackFillerApplicationHostedService"/> always stops the host
        /// on unexpected execution termination.
        /// </remarks>
        bool StopHostOnUnexpectedServiceTermination { get; }
    }
}
