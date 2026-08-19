using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace VideoPlayer.App.Helpers;

/// <summary>Text → QR bitmap, ready for an Image control. Used wherever a phone takes over.</summary>
public static class QrRender
{
    public static BitmapImage Render(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 10);

        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
