namespace PixelTransfer.Core;

public sealed class RgbImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public RgbImage(int width, int height, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 40_000_000) throw new ArgumentOutOfRangeException(nameof(width), "圖像尺寸超過限制。");
        Width = width; Height = height;
        Pixels = pixels ?? new byte[checked(width * height * 3)];
        if (Pixels.Length != checked(width * height * 3)) throw new ArgumentException("像素長度不符。");
    }
    public void FillRect(int x, int y, int width, int height, byte r, byte g, byte b)
    {
        for (int row = y; row < y + height; row++)
            for (int col = x; col < x + width; col++)
            {
                int p = (row * Width + col) * 3;
                Pixels[p] = r; Pixels[p + 1] = g; Pixels[p + 2] = b;
            }
    }
    public RgbImage Rotate(int turns)
    {
        turns = (turns % 4 + 4) % 4;
        var result = new RgbImage(turns % 2 == 0 ? Width : Height, turns % 2 == 0 ? Height : Width);
        for (int y = 0; y < result.Height; y++)
            for (int x = 0; x < result.Width; x++)
            {
                var (sx, sy) = SourceCoordinate(x, y, Width, Height, turns);
                Buffer.BlockCopy(Pixels, (sy * Width + sx) * 3, result.Pixels, (y * result.Width + x) * 3, 3);
            }
        return result;
    }
    public static (int X, int Y) SourceCoordinate(int x, int y, int physicalWidth, int physicalHeight, int turns) => turns switch
    {
        1 => (physicalWidth - 1 - y, x),
        2 => (physicalWidth - 1 - x, physicalHeight - 1 - y),
        3 => (y, physicalHeight - 1 - x),
        _ => (x, y)
    };
}

public readonly record struct FrameSize(int Width, int Height)
{
    public void Validate()
    {
        if (Width < 640 || Width > 3840 || Height < 360 || Height > 2160) throw new ArgumentException("圖像須介於 640 × 360 與 3840 × 2160。");
    }
    public int DataWidth => Width - 128;
    public int DataHeight => Height - 184;
    public int DataBytes { get { Validate(); return checked(DataWidth * DataHeight * 3); } }
    public int PayloadBytes => DataBytes - TransferPacket.Overhead;
    public override string ToString() => $"{Width} × {Height}";
}
