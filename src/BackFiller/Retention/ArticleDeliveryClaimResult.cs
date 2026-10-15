namespace VectorNNTP.BackFiller.Retention
{
    /// <summary>Outcome of <see cref="IArticleRetentionAuthority.TryClaimForDelivery"/>.</summary>
    internal enum ArticleDeliveryClaimKind : byte
    {
        /// <summary>The entry is exclusively claimed and its existing ArtData reference was returned.</summary>
        Claimed = 0,

        /// <summary>No indexed, unexpired entry matches the Message-ID. Nothing was claimed.</summary>
        NotFound = 1,

        /// <summary>
        /// The entry is indexed, but its ArtData cannot be sent as an RFC 3977 multiline block
        /// without changing bytes, or its stored header does not match the lookup octets.
        /// Nothing was claimed.
        /// </summary>
        NotFrameable = 2,

        /// <summary>Another delivery already holds the entry. Nothing was claimed and no RequestId was consumed.</summary>
        InDelivery = 3,
    }

    /// <summary>Result of one NNTP delivery claim attempt.</summary>
    internal readonly struct ArticleDeliveryClaimResult
    {
        private ArticleDeliveryClaimResult(
            ArticleDeliveryClaimKind kind,
            ArticleRetentionAuthority.ArticleDeliveryClaim? claim)
        {
            Kind = kind;
            Claim = claim;
        }

        /// <summary>Gets the claim classification.</summary>
        internal ArticleDeliveryClaimKind Kind { get; }

        /// <summary>Gets the claim when <see cref="Kind"/> is <see cref="ArticleDeliveryClaimKind.Claimed"/>.</summary>
        internal ArticleRetentionAuthority.ArticleDeliveryClaim? Claim { get; }

        /// <summary>Creates a claimed result.</summary>
        /// <param name="claim">Exclusive delivery claim. The caller commits or disposes it.</param>
        /// <returns>A result whose kind is <see cref="ArticleDeliveryClaimKind.Claimed"/>.</returns>
        internal static ArticleDeliveryClaimResult Claimed(ArticleRetentionAuthority.ArticleDeliveryClaim claim) =>
            new(ArticleDeliveryClaimKind.Claimed, claim);

        /// <summary>Creates a not-found result.</summary>
        /// <returns>A result with a null <see cref="Claim"/>.</returns>
        internal static ArticleDeliveryClaimResult NotFound() =>
            new(ArticleDeliveryClaimKind.NotFound, null);

        /// <summary>Creates a not-frameable result. The entry stays indexed.</summary>
        /// <returns>A result with a null <see cref="Claim"/>.</returns>
        internal static ArticleDeliveryClaimResult NotFrameable() =>
            new(ArticleDeliveryClaimKind.NotFrameable, null);

        /// <summary>Creates an in-delivery result. The existing claim is left in place.</summary>
        /// <returns>A result with a null <see cref="Claim"/>.</returns>
        internal static ArticleDeliveryClaimResult InDelivery() =>
            new(ArticleDeliveryClaimKind.InDelivery, null);
    }
}
