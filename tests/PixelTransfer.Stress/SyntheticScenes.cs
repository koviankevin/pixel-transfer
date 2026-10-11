using PixelTransfer.Core;

internal static class SyntheticScenes
{
    internal static TransferPacket Packet(bool dense = false)
    {
        var size = new FrameSize(640, 360);
        byte[] payload = new byte[dense ? size.PayloadBytes : 8192];
        new Random(152).NextBytes(payload);
        return new TransferPacket(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), 0, 1, size, payload);
    }

    internal static RgbImage Desktop(int width, int height)
    {
        var image = new RgbImage(width, height);
        Array.Fill(image.Pixels, (byte)95);
        // Deterministic synthetic windows, text-like strokes and a connected dark panel.
        var random = new Random(20261012);
        image.FillRect(width / 2, height / 2, width / 3, height / 3, 12, 12, 12);
        for (int i = 0; i < 1800; i++)
        {
            int x = random.Next(width - 32), y = random.Next(height - 12);
            byte c = (byte)(i % 3 == 0 ? 0 : random.Next(40, 230));
            image.FillRect(x, y, random.Next(3, 30), random.Next(1, 10), c, c, c);
        }
        return image;
    }

    internal static void Marker(RgbImage image, int x, int y, int size = 40)
    {
        // Separate each decoy from the background's connected components.
        image.FillRect(x - size / 2 - 4, y - size / 2 - 4, size + 8, size + 8, 255, 255, 255);
        image.FillRect(x - size / 2, y - size / 2, size, size, 0, 0, 0);
        image.FillRect(x - size * 3 / 10, y - size * 3 / 10, size * 6 / 10, size * 6 / 10, 255, 255, 255);
        image.FillRect(x - size * 3 / 20, y - size * 3 / 20, size * 3 / 10, size * 3 / 10, 48, 48, 240);
    }

    internal static RgbImage Grid(int width, int height, int count, int markerSize = 40)
    {
        var image = Desktop(width, height);
        for (int i = 0; i < count; i++)
            Marker(image, 80 + i % 6 * (width - 160) / 5, 80 + i / 6 * (height - 160) / 3, markerSize);
        return image;
    }

    internal static void Paste(RgbImage target, RgbImage frame, int x, int y)
    {
        for (int row = 0; row < frame.Height; row++)
            Buffer.BlockCopy(frame.Pixels, row * frame.Width * 3, target.Pixels,
                ((row + y) * target.Width + x) * 3, frame.Width * 3);
    }

    internal static RgbImage Scale(RgbImage frame, int scale)
    {
        var result = new RgbImage(frame.Width * scale, frame.Height * scale);
        for (int y = 0; y < result.Height; y++)
            for (int x = 0; x < result.Width; x++)
                Buffer.BlockCopy(frame.Pixels, ((y / scale) * frame.Width + x / scale) * 3,
                    result.Pixels, (y * result.Width + x) * 3, 3);
        return result;
    }

    internal static RgbImage Crop(RgbImage image, FrameBounds area)
    {
        var result = new RgbImage(area.Width, area.Height);
        for (int row = 0; row < area.Height; row++)
            Buffer.BlockCopy(image.Pixels, ((row + area.Y) * image.Width + area.X) * 3,
                result.Pixels, row * area.Width * 3, area.Width * 3);
        return result;
    }

    internal static RgbImage WithPacket(TransferPacket packet, int x = 1300, int y = 850)
    {
        var image = Desktop(2560, 1440);
        for (int i = 0; i < 21; i++) Marker(image, 80 + i % 7 * 340, 80 + i / 7 * 240);
        Paste(image, FrameCodec.Render(packet), x, y);
        return image;
    }
}
