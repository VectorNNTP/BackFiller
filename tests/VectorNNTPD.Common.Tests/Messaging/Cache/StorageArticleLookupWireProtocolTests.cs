using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Messaging.Cache;

namespace VectorNNTPD.Common.Tests.Messaging.Cache
{
    /// <summary>
    /// Locks version-1 storage article lookup request and response bytes, including rejection of a legacy URI field, and the cache fleet lookup topology names.
    /// </summary>
    public sealed class StorageArticleLookupWireProtocolTests
    {
        /// <summary>
        /// Asserts that a version-1 lookup request and response round-trip request id, article id, FQDN, and VATP port, and that the bytes contain neither a <c>uri</c> field nor a <c>targetStorage</c> field.
        /// </summary>
        [Fact]
        public void RequestAndResponse_RoundTrip()
        {
            var articleId = ArticleId.FromMessageId("<lookup@example.com>"u8);
            var request = new StorageArticleLookupRequest(1, Guid.NewGuid(), articleId);
            var requestBytes = StorageArticleLookupWireProtocol.SerializeRequestV1(request);
            Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(requestBytes, out var parsedRequest, out _));
            Assert.NotNull(parsedRequest);
            Assert.Equal(request.RequestId, parsedRequest.RequestId);
            Assert.Equal(articleId, parsedRequest.ArticleId);

            var response = new StorageArticleLookupResponse(
                1,
                request.RequestId,
                2,
                "cache02.usenet.ninja",
                articleId,
                1191);
            var responseBytes = StorageArticleLookupWireProtocol.SerializeResponseV1(response);
            Assert.True(StorageArticleLookupWireProtocol.TryParseResponseV1(responseBytes, out var parsedResponse, out _));
            Assert.NotNull(parsedResponse);
            Assert.Equal(response.Fqdn, parsedResponse.Fqdn);
            Assert.Equal(response.VatpPort, parsedResponse.VatpPort);
            Assert.Equal(response.ArticleId, parsedResponse.ArticleId);
            Assert.DoesNotContain("uri", Encoding.UTF8.GetString(responseBytes), StringComparison.Ordinal);
            Assert.DoesNotContain("targetStorage", Encoding.UTF8.GetString(requestBytes), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Asserts that a response payload which also carries <c>uri</c> fails parse, returns no response, and reports a reason that mentions <c>uri</c>.
        /// </summary>
        [Fact]
        public void TryParseResponseV1_RejectsLegacyUriField()
        {
            var articleId = ArticleId.FromMessageId("<lookup@example.com>"u8);
            var legacy = "vatp" + "://" + "cache02.usenet.ninja:1191/" + articleId.ToLowerHexString();
            var json = Encoding.UTF8.GetBytes(
                $$"""{"version":1,"requestId":"{{Guid.NewGuid()}}","serverId":2,"fqdn":"cache02.usenet.ninja","articleId":"{{articleId.ToLowerHexString()}}","vatpPort":1191,"uri":"{{legacy}}"}""");
            Assert.False(StorageArticleLookupWireProtocol.TryParseResponseV1(json, out var response, out var reason));
            Assert.Null(response);
            Assert.Contains("uri", reason, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Asserts the cache request fanout exchange name, the one-second lookup timeout and expiration, and that a storage-server queue name is <c>cache.</c> plus the lowercased FQDN.
        /// </summary>
        [Fact]
        public void TopologyConstants_DescribeFleetLookupBus()
        {
            Assert.Equal("cache.requests", CacheFleetTopology.RequestsExchangeName);
            Assert.Equal("fanout", CacheFleetTopology.FanoutExchangeType);
            Assert.Equal("1000", CacheFleetTopology.LookupExpirationMilliseconds);
            Assert.Equal(TimeSpan.FromSeconds(1), CacheFleetTopology.LookupTimeout);
            Assert.Equal(
                "cache.cache01.usenet.ninja",
                CacheFleetTopology.BuildStorageServerRequestQueueName("Cache01.Usenet.Ninja"));
        }
    }
}
