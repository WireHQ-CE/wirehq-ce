using QRCoder;

namespace WireHQ.Modules.WireGuard.Services;

/// <summary>Renders a config (or any text) to a PNG QR code for mobile enrollment (QRCoder, no native deps).</summary>
public interface IQrCodeService
{
    byte[] GeneratePng(string content);
}

public sealed class QrCodeService : IQrCodeService
{
    public byte[] GeneratePng(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(8);
    }
}
