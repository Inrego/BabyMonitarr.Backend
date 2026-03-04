using System.Text;
using System.Text.Json;
using QRCoder;

namespace BabyMonitarr.Backend.Services;

public static class QrCodeService
{
    public static string BuildQrPayload(string apiKey, string serverBaseUrl)
    {
        var json = JsonSerializer.Serialize(new { url = serverBaseUrl, key = apiKey });
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"babymonitarr://setup?d={base64}";
    }

    public static string GenerateQrPngBase64(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var qrCode = new PngByteQRCode(data);
        var pngBytes = qrCode.GetGraphic(8, new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 });
        return Convert.ToBase64String(pngBytes);
    }
}
