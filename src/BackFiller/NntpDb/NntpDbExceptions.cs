namespace VectorNNTP.BackFiller.NntpDb
{
    /// <summary>MySQL infrastructure failure that is not an authentication error.</summary>
    public sealed class NntpDbUnavailableException : Exception
    {
        /// <summary>Creates an infrastructure failure that is not an authentication error.</summary>
        /// <param name="message">Failure description. Must not include a connection string or credentials.</param>
        public NntpDbUnavailableException(string message)
            : base(message)
        {
        }

        /// <summary>Creates an infrastructure failure wrapping a provider exception.</summary>
        /// <param name="message">Failure description. Must not include a connection string or credentials.</param>
        /// <param name="innerException">The provider exception.</param>
        public NntpDbUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>MySQL authentication or authorization failure.</summary>
    public sealed class NntpDbAuthenticationException : Exception
    {
        /// <summary>Creates an authentication or authorization failure that must not be retried as unavailable.</summary>
        /// <param name="message">Failure description. Must not include a connection string or credentials.</param>
        public NntpDbAuthenticationException(string message)
            : base(message)
        {
        }

        /// <summary>Creates an authentication failure wrapping a MySQL provider exception.</summary>
        /// <param name="message">Failure description. Must not include a connection string or credentials.</param>
        /// <param name="innerException">The provider exception.</param>
        public NntpDbAuthenticationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
