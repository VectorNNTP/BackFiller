namespace VectorNNTP.BackFiller.Acme.Protocol
{
    /// <summary>
    /// External account binding credentials (RFC 8555 §7.3.4). Optional; VectorNNTP registration passes null
    /// when the directory does not require EAB.
    /// </summary>
    internal sealed class ExternalAccountBinding
    {
        /// <summary>HMAC key bytes. Returned by <see cref="GetHmacKey"/> without copying.</summary>
        private readonly byte[] _hmacKey;

        /// <summary>Stores EAB credentials decoded from a base64url HMAC key.</summary>
        /// <param name="keyId">CA key identifier stored as <see cref="KeyId"/>.</param>
        /// <param name="hmacKey">Base64url HMAC key. Invalid base64url throws <see cref="ArgumentException"/>.</param>
        private ExternalAccountBinding(string keyId, string hmacKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
            ArgumentException.ThrowIfNullOrWhiteSpace(hmacKey);

            KeyId = keyId;
            _hmacKey = DecodeKey(hmacKey);
        }

        /// <summary>Stores EAB credentials from raw HMAC key bytes (a defensive copy is kept).</summary>
        /// <param name="keyId">CA key identifier stored as <see cref="KeyId"/>.</param>
        /// <param name="hmacKey">Raw HMAC key. A copy is stored.</param>
        private ExternalAccountBinding(string keyId, byte[] hmacKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
            ArgumentNullException.ThrowIfNull(hmacKey);

            KeyId = keyId;
            _hmacKey = (byte[])hmacKey.Clone();
        }

        /// <summary>External account key identifier sent as JWS <c>externalAccountBinding.kid</c>.</summary>
        internal string KeyId { get; }

        /// <summary>Returns the HMAC key bytes for JWS signing.</summary>
        /// <returns>The stored key. The caller must not mutate it; it is not a copy.</returns>
        internal byte[] GetHmacKey() => _hmacKey;

        /// <summary>Decodes a base64url HMAC key.</summary>
        /// <param name="hmacKey">Base64url text.</param>
        /// <returns>The raw key bytes.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="hmacKey"/> is not valid base64url.</exception>
        private static byte[] DecodeKey(string hmacKey)
        {
            try
            {
                return Base64Url.Decode(hmacKey);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException("The HMAC key is not valid base64url.", nameof(hmacKey), ex);
            }
        }
    }
}
