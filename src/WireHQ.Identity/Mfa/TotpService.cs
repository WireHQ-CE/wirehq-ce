using OtpNet;
using QRCoder;
using WireHQ.Application.Abstractions.Security;

namespace WireHQ.Identity.Mfa;

/// <summary>
/// TOTP (RFC 6238) enrolment and verification via Otp.NET, with a QR code (QRCoder, no native
/// image deps) for authenticator-app setup. The shared secret is returned once at enrolment and
/// stored envelope-encrypted by the caller. (docs/04-security.md)
/// </summary>
public sealed class TotpService : ITotpService
{
    public TotpEnrolment CreateEnrolment(string accountName, string issuer = "WireHQ")
    {
        var secretBytes = KeyGeneration.GenerateRandomKey(20);
        var secret = Base32Encoding.ToString(secretBytes);

        var label = Uri.EscapeDataString($"{issuer}:{accountName}");
        var otpAuthUri =
            $"otpauth://totp/{label}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";

        return new TotpEnrolment(secret, otpAuthUri, GenerateQrCode(otpAuthUri));
    }

    public bool VerifyCode(string secret, string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var totp = new Totp(Base32Encoding.ToBytes(secret));
        return totp.VerifyTotp(code.Trim(), out _, VerificationWindow.RfcSpecifiedNetworkDelay);
    }

    private static string GenerateQrCode(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        return Convert.ToBase64String(png.GetGraphic(8));
    }
}
