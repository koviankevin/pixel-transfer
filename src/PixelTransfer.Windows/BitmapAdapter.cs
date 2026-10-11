using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelTransfer.Core;

namespace PixelTransfer.Windows;

internal static class BitmapAdapter
{
    public static BitmapSource Source(RgbImage image, double dpiX = 96, double dpiY = 96)
    {
        var source = BitmapSource.Create(image.Width, image.Height, dpiX, dpiY, PixelFormats.Rgb24, null, image.Pixels, image.Width * 3);
        source.Freeze(); return source;
    }
    public static RgbImage Load(string path)
    {
        using var file = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var original = decoder.Frames[0];
        if ((long)original.PixelWidth * original.PixelHeight > 40_000_000) throw new InvalidDataException("截圖尺寸超過限制。");
        var source = new FormatConvertedBitmap(original, PixelFormats.Rgb24, null, 0);
        byte[] bytes = new byte[checked(source.PixelWidth * source.PixelHeight * 3)];
        source.CopyPixels(bytes, source.PixelWidth * 3, 0);
        return new(source.PixelWidth, source.PixelHeight, bytes);
    }
    public static void Save(RgbImage image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Source(image)));
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write); encoder.Save(file);
    }
}
