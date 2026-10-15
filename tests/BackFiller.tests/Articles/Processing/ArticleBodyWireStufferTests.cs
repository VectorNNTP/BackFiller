using VectorNNTP.BackFiller.Articles.Processing;

namespace VectorNNTP.BackFiller.Tests.Articles.Processing
{
    public sealed class ArticleBodyWireStufferTests
    {
        [Fact]
        public void Body_lines_starting_with_a_dot_gain_exactly_one_dot()
        {
            var logical = Encoding.ASCII.GetBytes(
                "From: a@b.test\r\n.X-Dot: kept\r\n\r\n.only\r\n..two\r\n...three\r\nlater\r\n.last\r\n");
            var wire = ArticleBodyWireStuffer.StuffBody(logical);

            Assert.Equal(
                Encoding.ASCII.GetBytes(
                    "From: a@b.test\r\n.X-Dot: kept\r\n\r\n..only\r\n...two\r\n....three\r\nlater\r\n..last\r\n"),
                wire);
            Assert.False(wire.AsSpan().EndsWith(".\r\n.\r\n"u8));
        }

        [Fact]
        public void Header_dot_is_not_stuffed_and_an_unstuffed_body_is_the_same_array()
        {
            var logical = Encoding.ASCII.GetBytes("From: a@b.test\r\n.X-Dot: kept\r\n\r\nplain\n");
            var wire = ArticleBodyWireStuffer.StuffBody(logical);

            Assert.Same(logical, wire);
        }

        [Theory]
        [InlineData("", "")]
        [InlineData("\n", "\n")]
        [InlineData("\r\n", "\r\n")]
        [InlineData(".", "..")]
        [InlineData(".\r", "..\r")]
        [InlineData(".\n", "..\n")]
        [InlineData(".\r\n", "..\r\n")]
        [InlineData("..\r\n", "...\r\n")]
        [InlineData("...\r\n", "....\r\n")]
        [InlineData("a.b\r\n", "a.b\r\n")]
        [InlineData("plain\r\n.\r\n..x\n...y\r\n.last", "plain\r\n..\r\n...x\n....y\r\n..last")]
        public void Logical_body_lines_keep_one_stuffed_dot_at_the_line_start(string body, string stuffed)
        {
            var logical = Encoding.ASCII.GetBytes("From: a@b.test\r\n\r\n" + body);
            var wire = ArticleBodyWireStuffer.StuffBody(logical);

            Assert.Equal(Encoding.ASCII.GetBytes("From: a@b.test\r\n\r\n" + stuffed), wire);
        }

        [Fact]
        public void Lf_separator_and_mixed_endings_stuff_only_the_body()
        {
            var logical = Encoding.ASCII.GetBytes("From: a@b.test\n\n.\n..x\r\n");
            var wire = ArticleBodyWireStuffer.StuffBody(logical);

            Assert.Equal(Encoding.ASCII.GetBytes("From: a@b.test\n\n..\n...x\r\n"), wire);
        }
    }
}
