using VectorNNTP.BackFiller.Articles;
using VectorNNTP.BackFiller.Articles.Parsing;

namespace VectorNNTP.BackFiller.Retention
{
    /// <summary>
    /// Outcome of a VATP OPEN against a pending RequestId.
    /// </summary>
    internal enum VatpOpenKind : byte
    {
        /// <summary>RequestId+ArticleId matched; lease acquired; that RequestId consumed.</summary>
        Opened = 0,

        /// <summary>
        /// OPEN rejected (unknown/expired/cancelled/consumed/wrong ArticleId/reservation failed).
        /// Wrong ArticleId and reservation failure do not consume RequestId.
        /// </summary>
        Rejected = 1,
    }

    /// <summary>Result of <see cref="IArticleRetentionAuthority.TryOpenTransfer"/>.</summary>
    internal readonly struct VatpOpenResult : IDisposable
    {
        /// <summary>Pairs an OPEN classification with the lease that classification owns.</summary>
        /// <param name="kind"><see cref="VatpOpenKind.Opened"/> or <see cref="VatpOpenKind.Rejected"/>.</param>
        /// <param name="lease">
        /// Transfer lease when <paramref name="kind"/> is <see cref="VatpOpenKind.Opened"/>;
        /// <see langword="null"/> when rejected.
        /// </param>
        private VatpOpenResult(VatpOpenKind kind, VatpTransferLease? lease)
        {
            Kind = kind;
            Lease = lease;
        }

        /// <summary>Gets the OPEN classification.</summary>
        internal VatpOpenKind Kind { get; }

        /// <summary>Gets the transfer lease when <see cref="Kind"/> is <see cref="VatpOpenKind.Opened"/>.</summary>
        internal VatpTransferLease? Lease { get; }

        /// <summary>Creates an opened result.</summary>
        /// <param name="lease">Reader lease for the consumed RequestId.</param>
        /// <returns>A result whose <see cref="Kind"/> is <see cref="VatpOpenKind.Opened"/>.</returns>
        internal static VatpOpenResult Opened(VatpTransferLease lease) => new(VatpOpenKind.Opened, lease);

        /// <summary>Creates a rejected result (no lease).</summary>
        /// <returns>
        /// A result whose <see cref="Kind"/> is <see cref="VatpOpenKind.Rejected"/> and whose
        /// <see cref="Lease"/> is <see langword="null"/>.
        /// </returns>
        internal static VatpOpenResult Rejected() => new(VatpOpenKind.Rejected, null);

        /// <summary>Disposes <see cref="Lease"/> when this result opened a transfer.</summary>
        /// <remarks>
        /// A rejected result has no lease, so dispose does nothing. Disposing twice follows
        /// <see cref="VatpTransferLease.Dispose"/>, which runs the release callback only once.
        /// </remarks>
        public void Dispose() => Lease?.Dispose();
    }

    /// <summary>
    /// Lease over one retained article for a VATP transfer that has already started.
    /// </summary>
    /// <remarks>
    /// The authority drops its own <see cref="ArticleRecord"/> at the TTL deadline and then calls
    /// <see cref="Revoke"/>. Revoke clears this lease's article reference so the lease cannot be used
    /// to read further bytes. A frame already sliced from the article is a separate reference and is
    /// not cleared here. <see cref="Dispose"/> drops the reader count and any remaining local reference.
    /// </remarks>
    internal sealed class VatpTransferLease : IDisposable
    {
        /// <summary>
        /// Authority callback that drops the reader count. Invoked at most once.
        /// A null callback is ignored.
        /// </summary>
        private readonly Action? _release;

        /// <summary>Serializes revoke, dispose, and the revocation observer.</summary>
        private readonly Lock _revokeGate = new();

        /// <summary>Article bytes for this transfer. Cleared by <see cref="Revoke"/> and <see cref="Dispose"/>.</summary>
        private ArticleRecord? _record;

        /// <summary>Session callback invoked once when <see cref="Revoke"/> wins. Not invoked after dispose.</summary>
        private Action? _observer;

        /// <summary>1 after the first successful <see cref="Revoke"/>.</summary>
        private int _revoked;

        /// <summary>1 after the first <see cref="Dispose"/>.</summary>
        private int _disposed;

        /// <summary>Binds a retained record to the authority callback that drops its reader lease.</summary>
        /// <param name="record">Canonical record. This lease holds its own reference and does not copy the array.</param>
        /// <param name="selectedDateHeaderName">Date-family header name required for VATP META.</param>
        /// <param name="identity">Message-ID and ArticleId of the retained entry.</param>
        /// <param name="release">
        /// Called once from the first <see cref="Dispose"/>. The authority uses it to drop the reader.
        /// It does not extend the TTL and does not reattach ArtData after expiry.
        /// </param>
        internal VatpTransferLease(
            ArticleRecord record,
            NntpArticleHeaderName selectedDateHeaderName,
            ArticleIdentity identity,
            Action release)
        {
            _record = record;
            SelectedDateHeaderName = selectedDateHeaderName;
            Identity = identity;
            _release = release;
        }

        /// <summary>Date-family header name required for VATP META. Not cleared on revoke.</summary>
        internal NntpArticleHeaderName SelectedDateHeaderName { get; }

        /// <summary>Message-ID / ArticleId identity. Not cleared on revoke.</summary>
        internal ArticleIdentity Identity { get; }

        /// <summary><see langword="true"/> after <see cref="Revoke"/> has dropped this lease's article reference.</summary>
        internal bool IsRevoked => Volatile.Read(ref _revoked) == 1;

        /// <summary><see langword="true"/> while this lease still references article bytes.</summary>
        internal bool HoldsArticle
        {
            get
            {
                lock (_revokeGate)
                {
                    return _record is not null;
                }
            }
        }

        /// <summary>Gets the leased record.</summary>
        /// <exception cref="InvalidOperationException">The lease was revoked or disposed and no longer holds article bytes.</exception>
        internal ArticleRecord Record
        {
            get
            {
                lock (_revokeGate)
                {
                    if (_record is not { } record)
                    {
                        throw new InvalidOperationException("VATP transfer lease no longer holds article bytes.");
                    }

                    return record;
                }
            }
        }

        /// <summary>
        /// Registers the session callback that drops session-owned article aliases.
        /// </summary>
        /// <param name="observer">
        /// Invoked immediately when this lease is already revoked. Must not take the retention gate.
        /// A later <see cref="Revoke"/> invokes it once, unless <see cref="Dispose"/> has already run.
        /// </param>
        internal void SetRevocationObserver(Action observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            bool invoke;
            lock (_revokeGate)
            {
                _observer = observer;
                invoke = _revoked == 1 && _disposed == 0;
            }

            if (invoke)
            {
                observer();
            }
        }

        /// <summary>
        /// Drops this lease's article reference and notifies the session observer once.
        /// </summary>
        /// <remarks>
        /// Does not take the retention gate. A second call does nothing. Does not zero the backing array.
        /// </remarks>
        internal void Revoke()
        {
            Action? observer;
            lock (_revokeGate)
            {
                if (Interlocked.Exchange(ref _revoked, 1) == 1)
                {
                    return;
                }

                _record = null;
                observer = _disposed == 0 ? _observer : null;
            }

            observer?.Invoke();
        }

        /// <summary>Releases the reader lease once and drops any remaining article reference.</summary>
        /// <remarks>
        /// The first call invokes <see cref="_release"/>. Later calls return without invoking it.
        /// Does not by itself delete a Message-ID entry that has not expired.
        /// </remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            lock (_revokeGate)
            {
                _record = null;
                _observer = null;
            }

            _release?.Invoke();
        }
    }
}
