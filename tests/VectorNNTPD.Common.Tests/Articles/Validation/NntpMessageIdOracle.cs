namespace VectorNNTPD.Common.Tests.Articles.Validation
{
    /// <summary>
    /// Test-only scalar RFC 5536 §3.1.3 checker.
    /// </summary>
    /// <remarks>
    /// This type does not call <c>NntpMessageIdValidation</c> or the production character-class helpers.
    /// <c>atext</c> is the RFC 5322 §3.2.3 set stored in a lookup table.
    /// <c>mdtext</c> is RFC 5536 <c>%d33-61 / %d63-90 / %d94-126</c>.
    /// </remarks>
    internal static class NntpMessageIdOracle
    {
        /// <summary>
        /// Maximum Message-ID length in octets, including the angle brackets.
        /// </summary>
        private const int MaxLength = 250;

        /// <summary>
        /// Lookup of RFC 5322 <c>atext</c> indexed by octet value.
        /// </summary>
        private static readonly bool[] Atext = BuildAtext();

        /// <summary>
        /// Lookup of RFC 5536 <c>mdtext</c> indexed by octet value.
        /// </summary>
        private static readonly bool[] Mdtext = BuildMdtext();

        /// <summary>
        /// Returns whether <paramref name="messageId"/> is an RFC 5536 <c>msg-id</c> of 3 through 250 octets:
        /// a dot-atom, <c>@</c>, and either a dot-atom or a <c>[</c><c>mdtext</c><c>]</c> literal that ends the id.
        /// </summary>
        /// <param name="messageId">Candidate octets, including brackets when present.</param>
        /// <returns><see langword="false"/> when the length is outside 3..250 or the syntax check fails.</returns>
        internal static bool IsValid(ReadOnlySpan<byte> messageId)
        {
            if ((uint)(messageId.Length - 3) > (uint)(MaxLength - 3))
            {
                return false;
            }

            if (messageId[0] != (byte)'<' || messageId[^1] != (byte)'>')
            {
                return false;
            }

            var end = messageId.Length - 1;
            var index = 1;
            if (!TryDotAtom(messageId, end, ref index) || index >= end || messageId[index] != (byte)'@')
            {
                return false;
            }

            index++;
            if (index >= end)
            {
                return false;
            }

            if (messageId[index] == (byte)'[')
            {
                return TryLiteral(messageId, end, ref index);
            }

            return TryDotAtom(messageId, end, ref index) && index == end;
        }

        /// <summary>
        /// Consumes one or more atoms separated by single dots. A leading, trailing, or doubled dot fails.
        /// </summary>
        /// <param name="messageId">Candidate octets.</param>
        /// <param name="end">Index of the closing bracket, which is not consumed.</param>
        /// <param name="index">Start index, advanced past the dot-atom on success.</param>
        /// <returns><see langword="true"/> when at least one well-formed atom was consumed.</returns>
        private static bool TryDotAtom(ReadOnlySpan<byte> messageId, int end, ref int index)
        {
            if (!TryAtom(messageId, end, ref index))
            {
                return false;
            }

            while (index < end && messageId[index] == (byte)'.')
            {
                index++;
                if (!TryAtom(messageId, end, ref index))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Consumes one or more <c>atext</c> octets.
        /// </summary>
        /// <param name="messageId">Candidate octets.</param>
        /// <param name="end">Index that must not be consumed.</param>
        /// <param name="index">Start index, advanced past the atom on success.</param>
        /// <returns><see langword="false"/> when the current octet is missing or is not <c>atext</c>.</returns>
        private static bool TryAtom(ReadOnlySpan<byte> messageId, int end, ref int index)
        {
            if (index >= end || !Atext[messageId[index]])
            {
                return false;
            }

            index++;
            while (index < end && Atext[messageId[index]])
            {
                index++;
            }

            return true;
        }

        /// <summary>
        /// Consumes a domain literal that has already presented <c>[</c>, then requires <c>]</c> to be the last interior octet.
        /// </summary>
        /// <param name="messageId">Candidate octets.</param>
        /// <param name="end">Index of the closing angle bracket.</param>
        /// <param name="index">Index of <c>[</c>, advanced to <paramref name="end"/> on success.</param>
        /// <returns><see langword="true"/> when the literal is closed and nothing remains before the angle bracket.</returns>
        private static bool TryLiteral(ReadOnlySpan<byte> messageId, int end, ref int index)
        {
            index++;
            while (index < end && Mdtext[messageId[index]])
            {
                index++;
            }

            if (index >= end || messageId[index] != (byte)']')
            {
                return false;
            }

            index++;
            return index == end;
        }

        /// <summary>
        /// Builds the 256-slot <c>atext</c> table: letters, digits, and the RFC 5322 punctuation set.
        /// </summary>
        /// <returns>The lookup table.</returns>
        private static bool[] BuildAtext()
        {
            var table = new bool[256];
            for (var value = (byte)'A'; value <= (byte)'Z'; value++)
            {
                table[value] = true;
            }

            for (var value = (byte)'a'; value <= (byte)'z'; value++)
            {
                table[value] = true;
            }

            for (var value = (byte)'0'; value <= (byte)'9'; value++)
            {
                table[value] = true;
            }

            foreach (var value in "!#$%&'*+-/=?^_`{|}~"u8)
            {
                table[value] = true;
            }

            return table;
        }

        /// <summary>
        /// Builds the 256-slot <c>mdtext</c> table for octets 33-61, 63-90, and 94-126.
        /// </summary>
        /// <returns>The lookup table.</returns>
        private static bool[] BuildMdtext()
        {
            var table = new bool[256];
            for (var value = 33; value <= 61; value++)
            {
                table[value] = true;
            }

            for (var value = 63; value <= 90; value++)
            {
                table[value] = true;
            }

            for (var value = 94; value <= 126; value++)
            {
                table[value] = true;
            }

            return table;
        }
    }
}
