using VectorNNTPD.BackFillerService.Articles.Checksum;

namespace VectorNNTPD.BackFillerService.Articles.YEnc
{
    /// <summary>
    /// IEEE CRC-32 (polynomial <c>0xEDB88320</c>) used by yEnc trailer validation.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="IeeeCrc32"/>. This is not Castagnoli CRC32C. Coverage is
    /// the decoded yEnc payload, not canonical ArtData.
    /// </remarks>
    internal static class YEncCrc32
    {
        /// <summary>Computes the finalized IEEE CRC-32 of <paramref name="data"/>.</summary>
        /// <param name="data">Input bytes.</param>
        /// <returns>Finalized CRC-32.</returns>
        internal static uint Compute(ReadOnlySpan<byte> data) => IeeeCrc32.Compute(data);
    }
}
