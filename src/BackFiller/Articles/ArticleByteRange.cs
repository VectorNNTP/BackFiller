namespace VectorNNTP.BackFiller.Articles
{
    /// <summary>
    /// Integer offset/length into an <see cref="ArticleRecord"/> ArtData buffer.
    /// </summary>
    internal readonly struct ArticleByteRange : IEquatable<ArticleByteRange>
    {
        /// <summary>Sentinel with <see cref="Offset"/> -1 and <see cref="IsPresent"/> false for missing header fields.</summary>
        internal static ArticleByteRange Absent { get; } = new(-1, 0);

        /// <summary>
        /// Stores an inclusive offset and length into canonical ArtData, or an absent sentinel when <paramref name="offset"/> is -1.
        /// </summary>
        /// <param name="offset">Byte offset into ArtData, or -1 when absent.</param>
        /// <param name="length">Byte length of the field value.</param>
        internal ArticleByteRange(int offset, int length)
        {
            Offset = offset;
            Length = length;
        }

        /// <summary>Gets the byte offset into ArtData, or -1 when the field is absent.</summary>
        private int Offset { get; }

        /// <summary>Header value length in bytes; zero is allowed when <see cref="IsPresent"/> is true.</summary>
        private int Length { get; }

        /// <summary>True when <see cref="Offset"/> is non-negative and <see cref="Slice"/> may index ArtData.</summary>
        internal bool IsPresent => Offset >= 0;

        /// <summary>
        /// Slices <paramref name="artData"/> for this range.
        /// </summary>
        /// <param name="artData">Canonical article bytes that own this range.</param>
        /// <returns>The field bytes, or empty when the field is absent or zero-length.</returns>
        internal ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> artData)
        {
            if (!IsPresent || Length == 0)
            {
                return [];
            }

            return artData.Slice(Offset, Length);
        }

        /// <inheritdoc />
        public bool Equals(ArticleByteRange other) => Offset == other.Offset && Length == other.Length;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ArticleByteRange other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Offset, Length);

        /// <summary>True when both ranges have the same offset and length.</summary>
        public static bool operator ==(ArticleByteRange left, ArticleByteRange right) => left.Equals(right);

        /// <summary>True when the ranges differ in offset or length.</summary>
        public static bool operator !=(ArticleByteRange left, ArticleByteRange right) => !left.Equals(right);
    }
}
