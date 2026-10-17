using System.Text;

namespace VectorNNTPD.Common.Tests.Articles.Validation
{
    /// <summary>One prebuilt Message-ID candidate and the RFC result expected of it.</summary>
    /// <param name="Name">Stable failure label.</param>
    /// <param name="Bytes">Candidate octets, including brackets when present.</param>
    /// <param name="Expected"><see langword="true"/> when the bytes are an RFC 5536 <c>msg-id</c> of at most 250 octets.</param>
    internal readonly record struct MessageIdSample(string Name, byte[] Bytes, bool Expected);

    /// <summary>
    /// Message-ID bytes built once for the oracle comparison.
    /// </summary>
    /// <remarks>
    /// Lengths and lane positions refer to the first 16-byte vector window, which starts at the first
    /// interior octet. Lane 16 is the first octet after that window. Literal marks are also placed at
    /// offsets 0, 1, 15, and 16 of the <c>mdtext</c> run, which is where that run's own vector window sits.
    /// </remarks>
    internal static class NntpMessageIdCorpus
    {
        /// <summary>
        /// Offsets into the interior, after the opening bracket, where lane and literal-mark samples place a probed octet.
        /// Offsets 15, 16, 31, 32, 127, and 128 sit on 16-byte window edges.
        /// </summary>
        private static readonly int[] InteriorPositions = [0, 1, 14, 15, 16, 17, 30, 31, 32, 33, 126, 127, 128, 129];

        /// <summary>
        /// Interior lengths whose total Message-ID length, including both brackets, sits beside a vector-window boundary.
        /// </summary>
        private static readonly int[] BoundaryInteriors = [15, 16, 17, 31, 32, 33, 127, 128, 129];

        /// <summary>
        /// The corpus built once from the explicit, length, lane, and literal-mark samples.
        /// </summary>
        /// <value>The sample list assembled once from the explicit, boundary-length, lane, and literal-mark builders.</value>
        internal static IReadOnlyList<MessageIdSample> Samples { get; } = Build();

        /// <summary>
        /// Assembles the explicit, boundary-length, lane-structure, and literal-mark samples.
        /// </summary>
        /// <returns>The finished corpus.</returns>
        private static MessageIdSample[] Build()
        {
            var samples = new List<MessageIdSample>(512);
            AddExplicit(samples);
            AddBoundaryLengths(samples);
            AddLaneStructures(samples);
            AddLiteralMarks(samples);
            return samples.ToArray();
        }

        /// <summary>
        /// Adds named dot-atom and domain-literal Message-IDs, including empty literals and the short malformed forms.
        /// </summary>
        /// <param name="samples">Corpus being built.</param>
        private static void AddExplicit(List<MessageIdSample> samples)
        {
            AddAscii(samples, "valid-minimum", "<a@b>", true);
            AddAscii(samples, "valid-dot-atom", "<foo.bar@example.com>", true);
            AddAscii(samples, "valid-plus", "<foo+bar@example.com>", true);
            AddAscii(samples, "valid-ipv4-literal", "<foo@[1.2.3.4]>", true);
            AddAscii(samples, "valid-ipv6-literal", "<foo@[IPv6:2001:db8::1]>", true);
            AddAscii(samples, "valid-at-inside-literal", "<foo@[a@b]>", true);
            AddAscii(samples, "valid-empty-literal", "<a@[]>", true);
            AddAscii(samples, "valid-mdtext-specials", "<f@[.\"()@]>", true);

            AddAscii(samples, "malformed-right-trailing-dot", "<foo@example.>", false);
            AddAscii(samples, "malformed-unclosed-literal", "<foo@[>", false);
            AddAscii(samples, "malformed-unclosed-literal-with-at", "<foo@[a@b>", false);
            AddAscii(samples, "malformed-literal-trailing-atom", "<foo@[a]b>", false);
            AddAscii(samples, "malformed-literal-extra-bracket", "<foo@[a]]>", false);
            AddAscii(samples, "malformed-interior-gt", "<foo@bar>>", false);
            AddAscii(samples, "malformed-bytes-after-closing-bracket", "<foo@example.com>extra>", false);
            AddAscii(samples, "malformed-missing-lt", "foo@example.com>", false);
            AddAscii(samples, "malformed-missing-gt", "<foo@example.com", false);
            AddAscii(samples, "malformed-length-3", "<x>", false);
            AddAscii(samples, "malformed-length-4", "<ab>", false);
            AddAscii(samples, "malformed-length-4-at", "<x@>", false);
            Add(samples, "malformed-empty", [], false);
        }

        /// <summary>
        /// Adds dot-atom and literal ids at total lengths 249, 250, and 251, plus the window-edge interior lengths.
        /// Totals through 250 are expected valid; 251 is expected invalid.
        /// </summary>
        /// <param name="samples">Corpus being built.</param>
        private static void AddBoundaryLengths(List<MessageIdSample> samples)
        {
            Add(samples, "total-249-at-early", DotAtom(1, 245), true);
            Add(samples, "total-249-at-late", DotAtom(245, 1), true);
            Add(samples, "total-250-at-early", DotAtom(1, 246), true);
            Add(samples, "total-250-at-late", DotAtom(246, 1), true);
            Add(samples, "total-251-at-early", DotAtom(1, 247), false);
            Add(samples, "total-251-at-late", DotAtom(247, 1), false);
            Add(samples, "total-249-literal", Literal(1, 243), true);
            Add(samples, "total-250-literal", Literal(1, 244), true);
            Add(samples, "total-251-literal", Literal(1, 245), false);

            foreach (var interior in BoundaryInteriors)
            {
                var total = interior + 2;
                Add(samples, $"interior-{interior}-at-early", DotAtom(1, interior - 2), total <= 250);
                Add(samples, $"interior-{interior}-at-late", DotAtom(interior - 2, 1), total <= 250);
                Add(samples, $"interior-{interior}-literal", Literal(1, interior - 4), total <= 250);
            }
        }

        /// <summary>
        /// Adds one structural probe at each <see cref="InteriorPositions"/> offset: atext punctuation, dots, a second <c>@</c>,
        /// brackets, quotes, whitespace, controls, non-ASCII, and right-hand dots.
        /// </summary>
        /// <param name="samples">Corpus being built.</param>
        private static void AddLaneStructures(List<MessageIdSample> samples)
        {
            foreach (var position in InteriorPositions)
            {
                Add(samples, $"lane-plus-{position}", Inject(position, (byte)'+'), true);
                Add(samples, $"lane-dot-{position}", Inject(position, (byte)'.'), position >= 1);
                Add(samples, $"lane-at-separator-{position}", DotAtom(position, 24), position >= 1);
                Add(samples, $"lane-second-at-{position}", Inject(position, (byte)'@'), false);
                Add(samples, $"lane-open-bracket-{position}", OpenBracket(position), position >= 2);
                Add(samples, $"lane-close-bracket-{position}", CloseBracket(position), position >= 3);
                Add(
                    samples,
                    $"lane-close-bracket-extra-{position}",
                    position >= 3 ? CloseLiteral(position - 3, extra: true) : Inject(position, (byte)']'),
                    false);
                Add(samples, $"lane-quote-{position}", Inject(position, (byte)'"'), false);
                Add(samples, $"lane-backslash-{position}", Inject(position, (byte)'\\'), false);
                Add(samples, $"lane-space-{position}", Inject(position, (byte)' '), false);
                Add(samples, $"lane-tab-{position}", Inject(position, (byte)'\t'), false);
                Add(samples, $"lane-del-{position}", Inject(position, 0x7F), false);
                Add(samples, $"lane-control-{position}", Inject(position, 0x01), false);
                Add(samples, $"lane-non-ascii-{position}", Inject(position, 0x80), false);
                Add(samples, $"lane-gt-{position}", Inject(position, (byte)'>'), false);

                if (position >= 2)
                {
                    var atomsBeforeDot = position - 2;
                    Add(samples, $"lane-right-dot-{position}", RightDot(atomsBeforeDot, 8), atomsBeforeDot >= 1);
                    Add(samples, $"lane-right-trailing-dot-{position}", RightDot(atomsBeforeDot, 0), false);
                }
                Add(
                    samples,
                    $"lane-consecutive-dots-{position}",
                    ConsecutiveDots(position),
                    false);
            }

            Add(samples, "mdtext-window-close-in-last-lane", CloseLiteral(15, extra: false), true);
            Add(samples, "mdtext-window-close-after-window", CloseLiteral(16, extra: false), true);
            Add(samples, "mdtext-window-close-before-full-window", CloseLiteral(14, extra: false), true);
        }

        /// <summary>
        /// Adds domain-literal ids that place an allowed mdtext mark, or a forbidden mark, at each interior offset.
        /// </summary>
        /// <param name="samples">Corpus being built.</param>
        private static void AddLiteralMarks(List<MessageIdSample> samples)
        {
            byte[] validMarks = [(byte)'.', (byte)'@', (byte)'"', (byte)'(', (byte)')', (byte)':'];
            byte[] invalidMarks = [(byte)'[', (byte)']', (byte)'\\', (byte)'>', (byte)' '];
            foreach (var offset in InteriorPositions)
            {
                foreach (var mark in validMarks)
                {
                    Add(samples, $"mdtext-{mark:X2}-at-{offset}", LiteralMark(offset, mark, tail: 20), true);
                }

                foreach (var mark in invalidMarks)
                {
                    Add(samples, $"mdtext-bad-{mark:X2}-at-{offset}", LiteralMark(offset, mark, tail: 20), false);
                }
            }
        }

        /// <summary>
        /// Builds <c>&lt;</c>, <paramref name="left"/> <c>a</c> octets, <c>@</c>, <paramref name="right"/> <c>b</c> octets, and <c>&gt;</c>.
        /// </summary>
        /// <param name="left">Local-part length.</param>
        /// <param name="right">Domain length.</param>
        /// <returns>The dot-atom Message-ID bytes.</returns>
        private static byte[] DotAtom(int left, int right)
        {
            var bytes = new byte[left + right + 3];
            bytes[0] = (byte)'<';
            bytes.AsSpan(1, left).Fill((byte)'a');
            bytes[left + 1] = (byte)'@';
            bytes.AsSpan(left + 2, right).Fill((byte)'b');
            bytes[^1] = (byte)'>';
            return bytes;
        }

        /// <summary>
        /// Builds a Message-ID whose domain is a literal of <paramref name="mdtext"/> <c>x</c> octets.
        /// </summary>
        /// <param name="left">Local-part length before <c>@</c>.</param>
        /// <param name="mdtext">Number of octets between <c>[</c> and <c>]</c>.</param>
        /// <returns>The literal-domain Message-ID bytes.</returns>
        private static byte[] Literal(int left, int mdtext)
        {
            var bytes = new byte[left + mdtext + 5];
            bytes[0] = (byte)'<';
            bytes.AsSpan(1, left).Fill((byte)'a');
            var at = left + 1;
            bytes[at] = (byte)'@';
            bytes[at + 1] = (byte)'[';
            bytes.AsSpan(at + 2, mdtext).Fill((byte)'x');
            bytes[^2] = (byte)']';
            bytes[^1] = (byte)'>';
            return bytes;
        }

        /// <summary>
        /// Builds a bracketed id and writes <paramref name="value"/> at interior offset <paramref name="interiorIndex"/>, before a tail and <c>@b</c>.
        /// </summary>
        /// <param name="interiorIndex">Zero-based offset after the opening <c>&lt;</c>.</param>
        /// <param name="value">Octet placed at that offset.</param>
        /// <returns>The probed Message-ID bytes.</returns>
        private static byte[] Inject(int interiorIndex, byte value)
        {
            const int tail = 8;
            var bytes = new byte[interiorIndex + tail + 5];
            bytes[0] = (byte)'<';
            bytes.AsSpan(1, interiorIndex).Fill((byte)'a');
            bytes[1 + interiorIndex] = value;
            bytes.AsSpan(2 + interiorIndex, tail).Fill((byte)'a');
            var at = 2 + interiorIndex + tail;
            bytes[at] = (byte)'@';
            bytes[at + 1] = (byte)'b';
            bytes[at + 2] = (byte)'>';
            return bytes;
        }

        /// <summary>
        /// Places <c>[</c> at <paramref name="interiorIndex"/> as a domain-literal opener when the local part can be non-empty; otherwise injects <c>[</c> into an atom.
        /// </summary>
        /// <param name="interiorIndex">Interior offset of the bracket.</param>
        /// <returns>The probed Message-ID bytes.</returns>
        private static byte[] OpenBracket(int interiorIndex)
        {
            if (interiorIndex >= 2)
            {
                return Literal(interiorIndex - 1, 24);
            }

            return Inject(interiorIndex, (byte)'[');
        }

        /// <summary>
        /// Places a closing literal bracket at <paramref name="interiorIndex"/> when a literal can end there; otherwise injects <c>]</c> into an atom.
        /// </summary>
        /// <param name="interiorIndex">Interior offset of the bracket.</param>
        /// <returns>The probed Message-ID bytes.</returns>
        private static byte[] CloseBracket(int interiorIndex)
        {
            if (interiorIndex >= 3)
            {
                return CloseLiteral(interiorIndex - 3, extra: false);
            }

            return Inject(interiorIndex, (byte)']');
        }

        /// <summary>
        /// Builds <c>&lt;a@</c>, <paramref name="atomsBeforeDot"/> <c>b</c> octets, a dot, <paramref name="atomsAfterDot"/> <c>c</c> octets, and <c>&gt;</c>.
        /// </summary>
        /// <param name="atomsBeforeDot">Domain atom length before the dot. Zero produces a leading dot.</param>
        /// <param name="atomsAfterDot">Domain atom length after the dot. Zero produces a trailing dot.</param>
        /// <returns>The right-hand dot-atom bytes.</returns>
        private static byte[] RightDot(int atomsBeforeDot, int atomsAfterDot)
        {
            var bytes = new byte[atomsBeforeDot + atomsAfterDot + 5];
            bytes[0] = (byte)'<';
            bytes[1] = (byte)'a';
            bytes[2] = (byte)'@';
            bytes.AsSpan(3, atomsBeforeDot).Fill((byte)'b');
            var dot = 3 + atomsBeforeDot;
            bytes[dot] = (byte)'.';
            bytes.AsSpan(dot + 1, atomsAfterDot).Fill((byte)'c');
            bytes[^1] = (byte)'>';
            return bytes;
        }

        /// <summary>
        /// Injects a dot at <paramref name="interiorIndex"/> and a second dot in the following interior byte when that byte is not the closing bracket.
        /// </summary>
        /// <param name="interiorIndex">Interior offset of the first dot.</param>
        /// <returns>A Message-ID containing consecutive dots.</returns>
        private static byte[] ConsecutiveDots(int interiorIndex)
        {
            var bytes = Inject(interiorIndex, (byte)'.');
            if (interiorIndex + 1 < bytes.Length - 1)
            {
                bytes[2 + interiorIndex] = (byte)'.';
            }

            return bytes;
        }

        /// <summary>
        /// Builds <c>&lt;a@[</c>, <paramref name="mdtext"/> <c>x</c> octets, <c>]</c>, an optional extra <c>z</c>, and <c>&gt;</c>.
        /// </summary>
        /// <param name="mdtext">Literal body length.</param>
        /// <param name="extra"><see langword="true"/> to insert <c>z</c> between <c>]</c> and <c>&gt;</c>.</param>
        /// <returns>The literal Message-ID bytes.</returns>
        private static byte[] CloseLiteral(int mdtext, bool extra)
        {
            var bytes = new byte[mdtext + (extra ? 1 : 0) + 6];
            bytes[0] = (byte)'<';
            bytes[1] = (byte)'a';
            bytes[2] = (byte)'@';
            bytes[3] = (byte)'[';
            bytes.AsSpan(4, mdtext).Fill((byte)'x');
            var bracket = 4 + mdtext;
            bytes[bracket] = (byte)']';
            if (extra)
            {
                bytes[bracket + 1] = (byte)'z';
            }

            bytes[^1] = (byte)'>';
            return bytes;
        }

        /// <summary>
        /// Builds a domain literal that places <paramref name="mark"/> after <paramref name="offset"/> filler octets and before <paramref name="tail"/> more octets.
        /// </summary>
        /// <param name="offset">Filler length inside the literal before the mark.</param>
        /// <param name="mark">Octet under test.</param>
        /// <param name="tail">Filler length after the mark and before <c>]</c>.</param>
        /// <returns>The literal Message-ID bytes.</returns>
        private static byte[] LiteralMark(int offset, byte mark, int tail)
        {
            var bytes = new byte[offset + tail + 7];
            bytes[0] = (byte)'<';
            bytes[1] = (byte)'a';
            bytes[2] = (byte)'@';
            bytes[3] = (byte)'[';
            bytes.AsSpan(4, offset).Fill((byte)'x');
            bytes[4 + offset] = mark;
            bytes.AsSpan(5 + offset, tail).Fill((byte)'y');
            bytes[^2] = (byte)']';
            bytes[^1] = (byte)'>';
            return bytes;
        }

        /// <summary>
        /// Adds <paramref name="text"/> as an ASCII sample.
        /// </summary>
        /// <param name="samples">Corpus being built.</param>
        /// <param name="name">Stable sample label.</param>
        /// <param name="text">ASCII Message-ID text.</param>
        /// <param name="expected"><see langword="true"/> when the text is a valid msg-id of at most 250 octets.</param>
        private static void AddAscii(List<MessageIdSample> samples, string name, string text, bool expected) =>
            Add(samples, name, Encoding.ASCII.GetBytes(text), expected);

        /// <summary>
        /// Appends one <see cref="MessageIdSample"/>.
        /// </summary>
        /// <param name="samples">Corpus being built.</param>
        /// <param name="name">Stable sample label.</param>
        /// <param name="bytes">Candidate octets.</param>
        /// <param name="expected"><see langword="true"/> when those octets are a valid msg-id of at most 250 octets.</param>
        private static void Add(List<MessageIdSample> samples, string name, byte[] bytes, bool expected) =>
            samples.Add(new MessageIdSample(name, bytes, expected));
    }
}
