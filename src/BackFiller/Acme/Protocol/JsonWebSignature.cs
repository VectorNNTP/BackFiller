using System.Text.Json;

namespace VectorNNTP.BackFiller.Acme.Protocol
{
    /// <summary>Builds ACME JWS (RFC 8555 §6.2).</summary>
    internal static class JsonWebSignature
    {
        /// <summary>Encodes a signed JWS object for an ACME request.</summary>
        /// <param name="key">Account key. Supplies <c>alg</c>, the JWK when <paramref name="keyId"/> is null, and the RSA signature.</param>
        /// <param name="url">Protected-header <c>url</c>.</param>
        /// <param name="nonce">Protected-header <c>nonce</c>. Omitted from the header when null.</param>
        /// <param name="keyId">Protected-header <c>kid</c>. <see langword="null"/> embeds <paramref name="key"/>'s JWK instead.</param>
        /// <param name="payload">JSON payload. An empty string produces an empty JWS payload (POST-as-GET).</param>
        /// <returns>Flattened JWS JSON with <c>protected</c>, <c>payload</c>, and <c>signature</c>.</returns>
        internal static string Encode(AcmeAccountKey key, Uri url, string? nonce, string? keyId, string payload)
        {
            string protectedHeader = Base64Url.Encode(WriteProtectedHeader(key, url, nonce, keyId));
            string encodedPayload = payload.Length == 0 ? string.Empty : Base64Url.Encode(Encoding.UTF8.GetBytes(payload));

            byte[] signingInput = Encoding.ASCII.GetBytes(protectedHeader + "." + encodedPayload);
            string signature = Base64Url.Encode(key.Sign(signingInput));

            return $"{{\"protected\":\"{protectedHeader}\",\"payload\":\"{encodedPayload}\",\"signature\":\"{signature}\"}}";
        }

        /// <summary>
        /// Writes the protected header JSON: <c>alg</c>, either <c>jwk</c> or <c>kid</c>, optional <c>nonce</c>, and <c>url</c>.
        /// </summary>
        /// <param name="key">Account key whose algorithm and JWK are written.</param>
        /// <param name="url">Request URL.</param>
        /// <param name="nonce">Replay nonce. Omitted when null.</param>
        /// <param name="keyId">Account URL. <see langword="null"/> writes the JWK instead of <c>kid</c>.</param>
        /// <returns>UTF-8 JSON bytes, later base64url-encoded.</returns>
        private static byte[] WriteProtectedHeader(AcmeAccountKey key, Uri url, string? nonce, string? keyId)
        {
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream);

            writer.WriteStartObject();
            writer.WriteString("alg", key.SignatureAlgorithm);

            if (keyId is null)
            {
                writer.WritePropertyName("jwk");
                writer.WriteRawValue(key.Jwk, skipInputValidation: true);
            }
            else
            {
                writer.WriteString("kid", keyId);
            }

            if (nonce is not null)
            {
                writer.WriteString("nonce", nonce);
            }

            writer.WriteString("url", url.AbsoluteUri);
            writer.WriteEndObject();
            writer.Flush();

            return stream.ToArray();
        }
    }
}
