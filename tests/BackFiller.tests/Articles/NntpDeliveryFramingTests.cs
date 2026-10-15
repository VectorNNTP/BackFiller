using VectorNNTP.BackFiller.Articles.Processing;

namespace VectorNNTP.BackFiller.Tests.Articles
{
    public sealed class NntpDeliveryFramingTests
    {
        [Fact]
        public void Nul_is_not_frameable()
        {
            var article = "Subject: s\r\n\r\na\0b\r\n"u8.ToArray();
            Assert.False(NntpDeliveryFraming.IsFrameable(article));
            Assert.False(NntpDeliveryFraming.TryStuffHeaderLines(article, out var stuffed));
            Assert.Null(stuffed);
            Assert.Equal("Subject: s\r\n\r\na\0b\r\n"u8, article);
        }

        [Fact]
        public void A_dot_leading_line_without_a_header_body_separator_is_not_frameable()
        {
            var article = ".X-Dot: kept\r\n"u8.ToArray();
            Assert.False(NntpDeliveryFraming.IsFrameable(article));
            Assert.False(NntpDeliveryFraming.TryStuffHeaderLines(article, out var stuffed));
            Assert.Null(stuffed);
            Assert.Equal(".X-Dot: kept\r\n"u8, article);
        }

        [Fact]
        public void A_crlf_article_without_a_separator_or_a_dot_line_is_sent_unchanged()
        {
            var article = "Subject: s\r\n"u8.ToArray();
            Assert.True(NntpDeliveryFraming.IsFrameable(article));
            Assert.False(NntpDeliveryFraming.TryStuffHeaderLines(article, out var stuffed));
            Assert.Null(stuffed);
        }
    }
}
