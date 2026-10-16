using VectorNNTP.BackFiller.Accounts;

namespace VectorNNTP.BackFiller.Tests.TestDoubles
{
    /// <summary>
    /// In-memory IProviderAccountSource with configurable rows, delays, and query failures.
    /// </summary>

    internal sealed class FakeProviderAccountSource : IProviderAccountSource
    {
        /// <summary>Protects reads and writes of the in-memory row set exposed through <see cref="Rows"/>.</summary>
        private readonly object _gate = new();
        /// <summary>Backing store for query results; assign via <see cref="Rows"/> rather than directly.</summary>
        private IReadOnlyList<ProviderAccountRow> _rows = [];
        /// <summary>Number of <see cref="QueryAsync"/> calls observed.</summary>
        private int _queryCount;

        /// <summary>When set, thrown after optional blocking once cancellation is checked; no database I/O.</summary>
        public Exception? QueryException { get; set; }

        /// <summary>When set, <see cref="QueryAsync"/> waits on this task starting at the query indexed by <see cref="BlockAfterQueryCount"/>.</summary>
        public TaskCompletionSource? Block { get; set; }

        /// <summary>1-based query index at which an optional wait on <see cref="Block"/> begins; default is the first query.</summary>
        public int BlockAfterQueryCount { get; set; } = 1;

        /// <summary>Total <see cref="QueryAsync"/> invocations observed, including those that block or throw.</summary>
        public int QueryCount => Volatile.Read(ref _queryCount);

        /// <summary>Rows returned from queries; does not hit a database.</summary>
        public IReadOnlyList<ProviderAccountRow> Rows
        {
            get
            {
                lock (_gate)
                {
                    return _rows;
                }
            }

            set
            {
                ArgumentNullException.ThrowIfNull(value);
                lock (_gate)
                {
                    _rows = value;
                }
            }
        }

        /// <summary>
        /// Increments the query counter, optionally waits on <see cref="Block"/>, then returns <see cref="Rows"/> or throws <see cref="QueryException"/>.
        /// Does not execute SQL or cache results between calls.
        /// </summary>
        /// <param name="cancellationToken">Honored during blocking and before returning rows.</param>
        /// <returns>The current in-memory row list snapshot.</returns>
        public async Task<IReadOnlyList<ProviderAccountRow>> QueryAsync(CancellationToken cancellationToken)
        {
            var queryCount = Interlocked.Increment(ref _queryCount);
            if (Block is not null && queryCount >= BlockAfterQueryCount)
            {
                await Block.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (QueryException is not null)
            {
                throw QueryException;
            }

            return Rows;
        }
    }
}
