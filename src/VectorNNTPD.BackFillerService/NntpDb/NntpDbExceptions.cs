namespace VectorNNTPD.BackFillerService.NntpDb
{
    /// <summary>MySQL infrastructure failure that is not an authentication error.</summary>
    internal sealed class NntpDbUnavailableException : Exception
    {
        /// <summary>Creates an infrastructure failure that is not an authentication error.</summary>
        /// <param name="message">Failure description. Must not include a connection string or credentials.</param>
        internal NntpDbUnavailableException(string message)
            : base(message)
        {
        }

        /// <summary>Creates an infrastructure failure wrapping a provider exception.</summary>
        /// <param name="message">Failure description. Must not include a connection string or credentials.</param>
        /// <param name="innerException">The provider exception.</param>
        internal NntpDbUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>MySQL authentication or authorization failure.</summary>
    internal sealed class NntpDbAuthenticationException : Exception
    {
        /// <summary>Creates an authentication or authorization failure that must not be retried as unavailable.</summary>
        /// <param name="message">Failure description. Must not include a connection string or credentials.</param>
        internal NntpDbAuthenticationException(string message)
            : base(message)
        {
        }

        /// <summary>Creates an authentication failure wrapping a MySQL provider exception.</summary>
        /// <param name="message">Failure description. Must not include a connection string or credentials.</param>
        /// <param name="innerException">The provider exception.</param>
        internal NntpDbAuthenticationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
