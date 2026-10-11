namespace PixelTransfer.Core;

public readonly record struct FrameBounds(int X, int Y, int Width, int Height);
public sealed record LocatedFrame(FrameSize Size, byte[] Bytes, FrameBounds Bounds);
public sealed record ProbeResult(FrameSize Size, int ChangedChannels, int ChangedPixels, int MaxError)
{
    public bool Passed => ChangedChannels == 0;
}

public static partial class FrameCodec
{
    public static RgbImage Render(TransferPacket packet) => RenderBytes(packet.Serialize(), packet.Size);
    public static RgbImage RenderBytes(byte[] bytes, FrameSize size)
    {
        if (bytes.Length > size.DataBytes) throw new ArgumentException("資料超過圖像容量。");
        var image = new RgbImage(size.Width, size.Height);
        Array.Fill(image.Pixels, (byte)255);
        (int X, int Y)[] points = [(36, 36), (size.Width - 36, 36), (36, size.Height - 36)];
        byte[][] colors = [[48, 48, 240], [48, 240, 48], [240, 48, 48]];
        for (int i = 0; i < points.Length; i++)
        {
            var (x, y) = points[i];
            image.FillRect(x - 20, y - 20, 40, 40, 0, 0, 0);
            image.FillRect(x - 12, y - 12, 24, 24, 255, 255, 255);
            image.FillRect(x - 6, y - 6, 12, 12, colors[i][0], colors[i][1], colors[i][2]);
        }
        int swatch = Math.Min(40, (size.Width - 160) / 16 - 4);
        for (int i = 0; i < 16; i++)
        {
            byte c = (byte)(i * 17);
            image.FillRect((size.Width - 16 * (swatch + 4)) / 2 + i * (swatch + 4), 32, swatch, 40, c, c, c);
        }
        for (int row = 0; row < size.DataHeight; row++)
        {
            int dst = ((row + 104) * size.Width + 64) * 3;
            int src = row * size.DataWidth * 3;
            int copy = Math.Clamp(bytes.Length - src, 0, size.DataWidth * 3);
            image.Pixels.AsSpan(dst, size.DataWidth * 3).Clear();
            if (copy > 0) bytes.AsSpan(src, copy).CopyTo(image.Pixels.AsSpan(dst));
        }
        return image;
    }

    // Public calibration pattern, not cryptographic key material.
    public static byte[] ProbeBytes(FrameSize size)
    {
        byte[] data = new byte[size.DataBytes];
        uint state = 0x50435431;
        for (int i = 0; i < data.Length; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            data[i] = (byte)state;
        }
        return data;
    }
    public static RgbImage Probe(FrameSize size) => RenderBytes(ProbeBytes(size), size);
    public static ProbeResult? Measure(RgbImage image)
    {
        ProbeResult? best = null;
        foreach (var frame in Locate(image))
        {
            byte[] expected = ProbeBytes(frame.Size);
            int changed = 0, pixels = 0, max = 0;
            for (int p = 0; p < expected.Length; p += 3)
            {
                bool different = false;
                for (int c = 0; c < 3; c++)
                {
                    int delta = Math.Abs(expected[p + c] - frame.Bytes[p + c]);
                    if (delta > 0) { changed++; different = true; max = Math.Max(max, delta); }
                }
                if (different) pixels++;
            }
            if (best == null || changed < best.ChangedChannels) best = new(frame.Size, changed, pixels, max);
        }
        return best;
    }
    public static TransferPacket? Read(RgbImage image) => Inspect(image).Packets.FirstOrDefault()?.Packet;

    private readonly record struct Point(double X, double Y, int Width, int Height);
    private static List<Point> FindMarkers(RgbImage image)
    {
        int width = image.Width, height = image.Height;
        byte[] rgb = image.Pixels;
        var visited = new bool[width * height];
        var queue = new Queue<int>();
        var found = new List<Point>();
        bool Dark(int p) => rgb[p * 3] < 28 && rgb[p * 3 + 1] < 28 && rgb[p * 3 + 2] < 28;
        for (int start = 0; start < visited.Length; start++)
        {
            if (visited[start] || !Dark(start)) continue;
            visited[start] = true; queue.Enqueue(start);
            int minX = width, minY = height, maxX = 0, maxY = 0, count = 0;
            while (queue.TryDequeue(out int p))
            {
                int x = p % width, y = p / width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); count++;
                Visit(x > 0 ? p - 1 : -1); Visit(x + 1 < width ? p + 1 : -1);
                Visit(y > 0 ? p - width : -1); Visit(y + 1 < height ? p + width : -1);
            }
            int mw = maxX - minX + 1, mh = maxY - minY + 1;
            if (mw < 10 || mh < 10 || mw > 160 || mh > 160 || Math.Abs(mw - mh) > 2) continue;
            if (count < mw * mh * 0.35 || count > mw * mh * 0.85) continue;
            int centerDark = 0;
            int samples = 0;
            for (int y = minY + mh / 3; y < minY + mh * 2 / 3; y++)
                for (int x = minX + mw / 3; x < minX + mw * 2 / 3; x++)
                { samples++; if (Dark(y * width + x)) centerDark++; }
            if (centerDark > samples * 0.15) continue;
            int center = (((minY + maxY) / 2) * width + (minX + maxX) / 2) * 3;
            int high = Math.Max(rgb[center], Math.Max(rgb[center + 1], rgb[center + 2]));
            int low = Math.Min(rgb[center], Math.Min(rgb[center + 1], rgb[center + 2]));
            if (high - low < 60) continue; // The three finder centers are colored.
            found.Add(new(minX + mw / 2.0, minY + mh / 2.0, mw, mh));
            if (found.Count > 24) return [];
        }
        return found;

        void Visit(int p)
        {
            if (p < 0 || visited[p] || !Dark(p)) return;
            visited[p] = true; queue.Enqueue(p);
        }
    }

    public static IEnumerable<LocatedFrame> Locate(RgbImage image) => LocateNative(image, FindMarkers(image));
    private static IEnumerable<LocatedFrame> LocateNative(RgbImage image, List<Point> markers, bool packetOnly = false)
    {
        var points = markers.Where(p => p.Width == 40 && p.Height == 40).ToArray();
        foreach (var elbow in points)
            foreach (var horizontal in points)
                foreach (var vertical in points)
                {
                    if (elbow == horizontal || elbow == vertical || horizontal == vertical) continue;
                    if (horizontal.Y != elbow.Y || vertical.X != elbow.X) continue;
                    int dx = (int)(horizontal.X - elbow.X), dy = (int)(vertical.Y - elbow.Y);
                    if (Math.Abs(dx) < 150 || Math.Abs(dy) < 150) continue;
                    int turns = dx > 0 ? (dy > 0 ? 0 : 3) : (dy > 0 ? 1 : 2);
                    int pw = Math.Abs(dx) + 72, ph = Math.Abs(dy) + 72;
                    var size = turns % 2 == 0 ? new FrameSize(pw, ph) : new FrameSize(ph, pw);
                    if (size.Width < 640 || size.Width > 3840 || size.Height < 360 || size.Height > 2160) continue;
                    int left = (int)Math.Min(elbow.X, horizontal.X) - 36, top = (int)Math.Min(elbow.Y, vertical.Y) - 36;
                    if (left < 0 || top < 0 || left + pw > image.Width || top + ph > image.Height) continue;
                    // Inspect only needs a validated packet (or a probe signature), not the
                    // full data capacity of every geometrically plausible rectangle.
                    byte[] data;
                    if (packetOnly)
                    {
                        byte[] header = new byte[36];
                        Copy(header);
                        int length = PacketByteCount(header, size);
                        if (length == 0) data = header; // preserve invalid/calibration diagnostics
                        else { data = new byte[length]; Copy(data); }
                    }
                    else { data = new byte[size.DataBytes]; Copy(data); }

                    void Copy(byte[] destination)
                    {
                        for (int p = 0; p < (destination.Length + 2) / 3; p++)
                        {
                            var (sx, sy) = RgbImage.SourceCoordinate(64 + p % size.DataWidth,
                                104 + p / size.DataWidth, pw, ph, turns);
                            int src = ((top + sy) * image.Width + left + sx) * 3;
                            for (int c = 0; c < 3 && p * 3 + c < destination.Length; c++)
                                destination[p * 3 + c] = image.Pixels[src + c];
                        }
                    }
                    yield return new LocatedFrame(size, data, new(left, top, pw, ph));
                }
    }
}

