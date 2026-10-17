using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests.Configuration
{
    /// <summary>
    /// Locks the shared ServerId required/range matrix.
    /// </summary>
    public sealed class ServerIdRulesTests
    {
        /// <summary>
        /// Asserts that <see cref="ServerIdRules.MinimumInclusive"/> is 1 and <see cref="ServerIdRules.MaximumInclusive"/> is 255.
        /// </summary>
        [Fact]
        public void Bounds_are_one_through_two_hundred_fifty_five()
        {
            Assert.Equal(1, ServerIdRules.MinimumInclusive);
            Assert.Equal(255, ServerIdRules.MaximumInclusive);
        }

        /// <summary>
        /// Asserts that a null server id is <see cref="ServerIdValidationStatus.Missing"/> while zero is present but <see cref="ServerIdValidationStatus.OutOfRange"/>.
        /// </summary>
        [Fact]
        public void Classify_treats_missing_as_distinct_from_zero()
        {
            Assert.True(ServerIdRules.IsMissing(null));
            Assert.False(ServerIdRules.IsMissing(0));
            Assert.Equal(ServerIdValidationStatus.Missing, ServerIdRules.Classify(null));
            Assert.Equal(ServerIdValidationStatus.OutOfRange, ServerIdRules.Classify(0));
        }

        /// <summary>
        /// Asserts that an inclusive in-range server id is valid and that <see cref="ServerIdRules.Validate"/> returns no failure for the caller-supplied key.
        /// </summary>
        /// <param name="serverId">Server id expected to be accepted.</param>
        [Theory]
        [InlineData(1)]
        [InlineData(8)]
        [InlineData(50)]
        [InlineData(99)]
        [InlineData(100)]
        [InlineData(255)]
        public void Classify_accepts_inclusive_range(int serverId)
        {
            Assert.True(ServerIdRules.IsInRange(serverId));
            Assert.Equal(ServerIdValidationStatus.Valid, ServerIdRules.Classify(serverId));
            Assert.Null(ServerIdRules.Validate(serverId, "ServerId"));
        }

        /// <summary>
        /// Asserts that a server id outside 1–255 is out of range and that the validation failure names that inclusive range.
        /// </summary>
        /// <param name="serverId">Server id expected to be rejected.</param>
        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(256)]
        [InlineData(257)]
        [InlineData(int.MaxValue)]
        public void Classify_rejects_out_of_range(int serverId)
        {
            Assert.False(ServerIdRules.IsInRange(serverId));
            Assert.Equal(ServerIdValidationStatus.OutOfRange, ServerIdRules.Classify(serverId));
            var failure = ServerIdRules.Validate(serverId, "ServerId");
            Assert.Contains("1–255", failure, StringComparison.Ordinal);
        }

        /// <summary>
        /// Asserts that a missing server id fails as required, names only the supplied <c>ServerId</c> key, and does not mention application environment prefixes.
        /// </summary>
        [Fact]
        public void Validate_missing_names_only_the_caller_supplied_key()
        {
            var failure = ServerIdRules.Validate(null, "ServerId");
            Assert.Contains("required", failure, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ServerId", failure, StringComparison.Ordinal);
            Assert.DoesNotContain("BACKFILLER__", failure, StringComparison.Ordinal);
            Assert.DoesNotContain("NNTPD__", failure, StringComparison.Ordinal);
            Assert.DoesNotContain("VECTOR__", failure, StringComparison.Ordinal);
        }
    }
}
