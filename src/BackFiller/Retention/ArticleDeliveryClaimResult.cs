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
        /// The entry is indexed, but its ArtData is not a CRLF-only multiline block, a dot-leading line
        /// cannot be classified as a header, or its stored header does not match the lookup octets.
        /// Nothing was claimed.
        /// </summary>
        NotFrameable = 2,

        /// <summary>Another delivery already holds the entry. Nothing was claimed and no RequestId was consumed.</summary>
        InDelivery = 3,
    }

    /// <summary>Result of one NNTP delivery claim attempt.</summary>
    internal readonly struct ArticleDeliveryClaimResult
    {
        /// <summary>
        /// Stores one claim outcome. <paramref name="claim"/> is non-null only when <paramref name="kind"/> is <see cref="ArticleDeliveryClaimKind.Claimed"/>.
        /// </summary>
        /// <param name="kind">Classification returned to the listener.</param>
        /// <param name="claim">Exclusive claim when delivery may proceed; otherwise null.</param>
        private ArticleDeliveryClaimResult(
            ArticleDeliveryClaimKind kind,
            ArticleRetentionAuthority.ArticleDeliveryClaim? claim)
        {
            Kind = kind;
            Claim = claim;
        }

        /// <summary>
        /// Listener-facing outcome of <see cref="IArticleRetentionAuthority.TryClaimForDelivery"/>.
        /// Rejections leave <see cref="Claim"/> null and do not consume a RequestId.
        /// </summary>
        internal ArticleDeliveryClaimKind Kind { get; }

        /// <summary>
        /// Exclusive delivery hold when <see cref="Kind"/> is <see cref="ArticleDeliveryClaimKind.Claimed"/>.
        /// The caller must <see cref="ArticleRetentionAuthority.ArticleDeliveryClaim.Commit"/> after a complete NNTP write or dispose to roll back.
        /// </summary>
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
