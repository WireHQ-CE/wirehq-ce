using System.Buffers.Binary;
using System.Buffers.Text;
using System.Text;
using NSec.Cryptography;

namespace WireHQ.Licensing.Paseto;

/// <summary>
/// PASETO <c>v4.public</c> — Ed25519-signed, versioned, tamper-evident tokens, per the paseto.io v4
/// specification. This is the <b>standard</b> construction, not a bespoke scheme: a domain-separated
/// header (<c>v4.public.</c>), the spec's Pre-Authentication Encoding (PAE), and an Ed25519 detached
/// signature over it. Correctness is pinned to the official PASETO test vectors
/// (<c>PasetoV4PublicVectorTests</c>), so any drift fails the build.
///
/// It is assembled on the codebase's existing libsodium binding (<see cref="SignatureAlgorithm.Ed25519"/>
/// via NSec) rather than adding a second crypto stack. Ed25519 is deterministic (RFC 8032), so a given
/// message + key produce a byte-identical token — which is exactly why the known-answer vectors work.
/// The security rests entirely on the private key; nothing here is a secret.
///
/// Callers layer claims (the message, JSON) and key selection (the footer's <c>kid</c>) on top; this
/// type only knows bytes.
/// </summary>
public static class PasetoV4Public
{
    private const string Header = "v4.public.";
    private const int SignatureSize = 64; // Ed25519 signatures are 64 bytes.
    private static readonly byte[] HeaderBytes = Encoding.UTF8.GetBytes(Header);
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    /// <summary>
    /// Signs <paramref name="message"/> (with an optional authenticated <paramref name="footer"/> and
    /// <paramref name="implicitAssertion"/>) into a <c>v4.public</c> token.
    /// </summary>
    public static string Sign(
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> footer,
        Key signingKey,
        ReadOnlySpan<byte> implicitAssertion = default)
    {
        var preAuth = PreAuthenticationEncoding(HeaderBytes, message, footer, implicitAssertion);
        var signature = Algorithm.Sign(signingKey, preAuth); // 64 bytes

        // Body = message ‖ signature, base64url-encoded (unpadded) after the header.
        var body = new byte[message.Length + SignatureSize];
        message.CopyTo(body);
        signature.CopyTo(body.AsSpan(message.Length));

        var token = Header + Base64Url.EncodeToString(body);
        return footer.IsEmpty ? token : token + "." + Base64Url.EncodeToString(footer);
    }

    /// <summary>
    /// Reads the token's footer <b>without</b> verifying the signature — used only to select the
    /// verification key by its <c>kid</c>. This is safe: the footer is bound into the PAE, so a
    /// tampered footer makes the subsequent <see cref="TryVerify"/> fail. An attacker cannot forge a
    /// valid signature for any key id without the corresponding private key.
    /// </summary>
    public static bool TryReadFooter(string token, out byte[] footer)
    {
        footer = [];
        if (!TrySplit(token, out _, out var footerPart))
        {
            return false;
        }

        return footerPart.Length == 0 || TryBase64UrlDecode(footerPart, out footer);
    }

    /// <summary>
    /// Verifies a <c>v4.public</c> token against <paramref name="publicKey"/> and, on success, returns
    /// the signed message bytes. Returns <c>false</c> for any malformed input, a wrong/short body, or a
    /// signature that does not verify — never throws for attacker-controlled input.
    /// </summary>
    public static bool TryVerify(
        string token,
        PublicKey publicKey,
        out byte[] message,
        ReadOnlySpan<byte> implicitAssertion = default)
    {
        message = [];
        if (!TrySplit(token, out var payloadPart, out var footerPart))
        {
            return false;
        }

        if (!TryBase64UrlDecode(payloadPart, out var body) || body.Length < SignatureSize)
        {
            return false;
        }

        byte[] footer = [];
        if (footerPart.Length > 0 && !TryBase64UrlDecode(footerPart, out footer))
        {
            return false;
        }

        var messageSpan = body.AsSpan(0, body.Length - SignatureSize);
        var signature = body.AsSpan(body.Length - SignatureSize);
        var preAuth = PreAuthenticationEncoding(HeaderBytes, messageSpan, footer, implicitAssertion);

        if (!Algorithm.Verify(publicKey, preAuth, signature))
        {
            return false;
        }

        message = messageSpan.ToArray();
        return true;
    }

    /// <summary>
    /// Splits <c>v4.public.{payload}[.{footer}]</c>. Requires the exact header and a non-empty payload;
    /// rejects extra segments (a fourth <c>.</c>).
    /// </summary>
    private static bool TrySplit(string token, out string payload, out string footer)
    {
        payload = string.Empty;
        footer = string.Empty;
        if (token is null || !token.StartsWith(Header, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = token.AsSpan(Header.Length);
        var firstDot = rest.IndexOf('.');
        if (firstDot < 0)
        {
            payload = rest.ToString();
            return payload.Length > 0;
        }

        var footerSpan = rest[(firstDot + 1)..];
        if (footerSpan.IndexOf('.') >= 0)
        {
            return false; // A v4.public token has at most one footer segment.
        }

        payload = rest[..firstDot].ToString();
        footer = footerSpan.ToString();
        return payload.Length > 0;
    }

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = [];
        try
        {
            bytes = Base64Url.DecodeFromChars(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// PASETO Pre-Authentication Encoding: <c>LE64(n) ‖ (LE64(len) ‖ piece)*</c>. The length prefixes
    /// make the concatenation unambiguous, so a signature over the PAE authenticates the header, the
    /// message, the footer and the implicit assertion together — moving bytes between fields changes it.
    /// </summary>
    private static byte[] PreAuthenticationEncoding(
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> footer,
        ReadOnlySpan<byte> implicitAssertion)
    {
        const int pieceCount = 4;
        var total = 8 + (8 + header.Length) + (8 + message.Length) + (8 + footer.Length) + (8 + implicitAssertion.Length);
        var buffer = new byte[total];
        var span = buffer.AsSpan();

        var offset = 0;
        WriteLittleEndian64(span, ref offset, pieceCount);
        WritePiece(span, ref offset, header);
        WritePiece(span, ref offset, message);
        WritePiece(span, ref offset, footer);
        WritePiece(span, ref offset, implicitAssertion);
        return buffer;
    }

    private static void WritePiece(Span<byte> span, ref int offset, ReadOnlySpan<byte> piece)
    {
        WriteLittleEndian64(span, ref offset, piece.Length);
        piece.CopyTo(span[offset..]);
        offset += piece.Length;
    }

    private static void WriteLittleEndian64(Span<byte> span, ref int offset, long value)
    {
        // The spec clears the most significant bit for interoperability with languages lacking
        // unsigned 64-bit integers. Our lengths are tiny, so this only ever matters for conformance.
        var unsigned = (ulong)value & 0x7FFFFFFFFFFFFFFFUL;
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset, 8), unsigned);
        offset += 8;
    }
}
