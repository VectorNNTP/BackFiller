using VectorNNTPD.Common.Articles.Validation;
using Xunit.Abstractions;

namespace VectorNNTPD.Common.Tests.Articles.Validation
{
    /// <summary>
    /// Asserts that production Message-ID validation matches the scalar oracle on every corpus sample.
    /// </summary>
    public sealed class NntpMessageIdCorpusTests
    {
        /// <summary>
        /// xUnit output used to record the corpus size.
        /// </summary>
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Stores the xUnit output helper.
        /// </summary>
        /// <param name="output">Output helper supplied by the test runner.</param>
        public NntpMessageIdCorpusTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Requires a corpus of 400 to 2000 samples, and for each sample requires production validation,
        /// <see cref="NntpMessageIdOracle.IsValid"/>, and the sample's expected flag to agree.
        /// </summary>
        [Fact]
        public void Production_matches_independent_scalar_oracle()
        {
            var samples = NntpMessageIdCorpus.Samples;
            _output.WriteLine($"corpus={samples.Count}");
            Assert.InRange(samples.Count, 400, 2000);

            foreach (var sample in samples)
            {
                var actual = NntpMessageIdValidation.IsValidMessageId(sample.Bytes);
                var oracle = NntpMessageIdOracle.IsValid(sample.Bytes);
                Assert.True(
                    actual == oracle,
                    $"{sample.Name} production={actual} oracle={oracle} length={sample.Bytes.Length}");
                Assert.Equal(sample.Expected, actual);
            }
        }
    }
}
