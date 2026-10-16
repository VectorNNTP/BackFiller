using MySqlConnector;

namespace VectorNNTP.BackFiller.NntpDb
{
    /// <summary>MySqlConnector-backed logical connection. Dispose returns it to the provider pool.</summary>
    internal sealed class MySqlNntpDbSession : INntpDbSession
    {
        /// <summary>Open provider connection. Dispose returns it to the MySqlConnector pool.</summary>
        private readonly MySqlConnection _connection;

        /// <summary>
        /// Takes ownership of an already-open MySqlConnector connection. <see cref="DisposeAsync"/> returns it to the provider pool.
        /// </summary>
        /// <param name="connection">Open provider connection. This session owns its disposal.</param>
        internal MySqlNntpDbSession(MySqlConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            _connection = connection;
        }

        /// <inheritdoc />
        public async ValueTask<int> SelectOneAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var command = _connection.CreateCommand();
                command.CommandText = "SELECT 1";
                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return ConvertSelectOneScalar(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not NntpDbUnavailableException)
            {
                throw new NntpDbUnavailableException("MySQL health query SELECT 1 failed.", ex);
            }
        }

        /// <inheritdoc />
        public MySqlCommand CreateCommand() => _connection.CreateCommand();

        /// <inheritdoc />
        public ValueTask<MySqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            _connection.BeginTransactionAsync(cancellationToken);

        /// <inheritdoc />
        public ValueTask<MySqlTransaction> BeginTransactionAsync(
            System.Data.IsolationLevel isolationLevel,
            CancellationToken cancellationToken) =>
            _connection.BeginTransactionAsync(isolationLevel, cancellationToken);

        /// <inheritdoc />
        public ValueTask DisposeAsync() => _connection.DisposeAsync();

        /// <summary>Maps the <c>SELECT 1</c> scalar to an integer.</summary>
        /// <param name="result">Provider scalar.</param>
        /// <returns>The integer result.</returns>
        private static int ConvertSelectOneScalar(object? result) =>
            result switch
            {
                int value => value,
                long longValue => checked((int)longValue),
                _ => throw new NntpDbUnavailableException("MySQL health query SELECT 1 returned an unexpected result."),
            };
    }
}
