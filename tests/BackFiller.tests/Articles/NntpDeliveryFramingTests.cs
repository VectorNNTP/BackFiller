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

        [Fact]
        public void A_dot_leading_header_is_stuffed_and_the_retained_bytes_stay_unchanged()
        {
            var article = ".X-Dot: kept\r\n\r\n.only\r\n"u8.ToArray();
            var retained = article.ToArray();
            Assert.True(NntpDeliveryFraming.IsFrameable(article));
            Assert.True(NntpDeliveryFraming.TryStuffHeaderLines(article, out var stuffed));
            Assert.Equal("..X-Dot: kept\r\n\r\n.only\r\n"u8.ToArray(), stuffed);
            Assert.Equal(retained, article);
        }

        [Fact]
        public void A_bare_lf_in_a_dot_leading_header_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: kept\n\r\n\r\nbody\r\n"u8.ToArray());
        }

        [Fact]
        public void A_bare_cr_in_a_dot_leading_header_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: kept\rX\r\n\r\nbody\r\n"u8.ToArray());
        }

        [Fact]
        public void A_nul_in_a_dot_leading_header_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: ke\0pt\r\n\r\nbody\r\n"u8.ToArray());
        }

        [Fact]
        public void A_nul_in_the_body_of_a_dot_leading_header_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: kept\r\n\r\nbo\0dy\r\n"u8.ToArray());
        }

        [Fact]
        public void A_bare_lf_in_the_body_of_a_dot_leading_header_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: kept\r\n\r\nbo\ndy\r\n"u8.ToArray());
        }

        [Fact]
        public void A_bare_cr_in_the_body_of_a_dot_leading_header_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: kept\r\n\r\nbo\rdy\r\n"u8.ToArray());
        }

        [Fact]
        public void A_missing_header_body_separator_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: kept\r\nbody\r\n"u8.ToArray());
        }

        [Fact]
        public void An_lf_only_separator_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: kept\n\nbody\n"u8.ToArray());
        }

        private static void AssertRejected(byte[] article)
        {
            var retained = article.ToArray();
            Assert.False(NntpDeliveryFraming.IsFrameable(article));
            Assert.False(NntpDeliveryFraming.TryStuffHeaderLines(article, out var stuffed));
            Assert.Null(stuffed);
            Assert.Equal(retained, article);
        }
    }
}
