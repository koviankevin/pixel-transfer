using System.Buffers.Binary;

namespace PixelTransfer.Core;

public enum FrameReadStatus { NoMarkers, IncompleteMarkers, InvalidData, Calibration, Success }
public sealed record DetectedPacket(TransferPacket Packet, FrameBounds Bounds, double Scale);
public sealed record FrameInspection(IReadOnlyList<DetectedPacket> Packets, FrameReadStatus Status)
{
    public string Message => Status switch
    {
        FrameReadStatus.NoMarkers => "找不到定位點。請確認圖片在主螢幕、完整可見。",
        FrameReadStatus.IncompleteMarkers => "找到部分定位點，但無法定位完整圖像。請避免裁切、遮擋或過度縮放。",
        FrameReadStatus.InvalidData => "已找到三點圖像並嘗試還原，但資料校驗未通過。縮放或色彩變更可能已遺失資料；請改用 100% 顯示或匯入原始 PNG。",
        FrameReadStatus.Calibration => "這是 RDP 校驗圖，不含可解密的檔案；請到「RDP 校驗」檢查。",
        _ => "已找到並校驗資料圖。"
    };
}

public static partial class FrameCodec
{
    private static readonly FrameSize[] StandardSizes = Enumerable.Range(40, 201)
        .Select(n => new FrameSize(n * 16, n * 9)).ToArray();

    public static FrameInspection Inspect(RgbImage image)
    {
        var markers = FindMarkers(image);
        var packets = new List<DetectedPacket>();
        var status = markers.Count == 0 ? FrameReadStatus.NoMarkers : FrameReadStatus.IncompleteMarkers;
        var nativeBounds = new HashSet<FrameBounds>();
        foreach (var frame in LocateNative(image, markers, packetOnly: true))
        {
            status = FrameReadStatus.InvalidData;
            var packet = TransferPacket.Parse(frame.Bytes, frame.Size);
            if (packet != null)
            {
                packets.Add(new(packet, frame.Bounds, 1));
                nativeBounds.Add(frame.Bounds);
            }
            else if (IsProbeHeader(frame.Bytes)) status = FrameReadStatus.Calibration;
        }
        foreach (var elbow in markers)
            foreach (var horizontal in markers)
                foreach (var vertical in markers)
                {
                    if (elbow == horizontal || elbow == vertical || horizontal == vertical) continue;
                    if (Math.Abs(horizontal.Y - elbow.Y) > 1 || Math.Abs(vertical.X - elbow.X) > 1) continue;
                    if (Math.Abs(elbow.Width - horizontal.Width) > 2 || Math.Abs(elbow.Width - vertical.Width) > 2 ||
                        Math.Abs(elbow.Height - horizontal.Height) > 2 || Math.Abs(elbow.Height - vertical.Height) > 2) continue;
                    double dx = horizontal.X - elbow.X, dy = vertical.Y - elbow.Y;
                    if (Math.Abs(dx) < elbow.Width * 3 || Math.Abs(dy) < elbow.Height * 3) continue;
                    if (status != FrameReadStatus.Calibration) status = FrameReadStatus.InvalidData;
                    int turns = dx > 0 ? (dy > 0 ? 0 : 3) : (dy > 0 ? 1 : 2);
                    double approxX = (elbow.Width + horizontal.Width + vertical.Width) / 120.0;
                    double approxY = (elbow.Height + horizontal.Height + vertical.Height) / 120.0;
                    int estimatedW = (int)Math.Round(Math.Abs(dx) / approxX + 72);
                    int estimatedH = (int)Math.Round(Math.Abs(dy) / approxY + 72);
                    var estimated = turns % 2 == 0 ? new FrameSize(estimatedW, estimatedH) : new FrameSize(estimatedH, estimatedW);
                    // The UI emits 16:9 frames. Also try the measured size for custom frames.
                    // Try the estimate once, then the fixed supported sizes without
                    // rebuilding a LINQ pipeline and Distinct set for every marker triple.
                    for (int candidate = -1; candidate < StandardSizes.Length; candidate++)
                    {
                        var size = candidate < 0 ? estimated : StandardSizes[candidate];
                        if (candidate >= 0 && size == estimated) continue;
                        if (size.Width < 640 || size.Height < 360 || size.Width > 3840 || size.Height > 2160) continue;
                        int pw = turns % 2 == 0 ? size.Width : size.Height, ph = turns % 2 == 0 ? size.Height : size.Width;
                        double sx = Math.Abs(dx) / (pw - 72), sy = Math.Abs(dy) / (ph - 72);
                        if (sx < 0.25 || sy < 0.25 || sx > 4 || sy > 4 || Math.Abs(sx - sy) > Math.Max(sx, sy) * 0.01) continue;
                        if (Math.Abs(sx - approxX) * 40 > 2.1 || Math.Abs(sy - approxY) * 40 > 2.1) continue;
                        double left = Math.Min(elbow.X, horizontal.X) - 36 * sx, top = Math.Min(elbow.Y, vertical.Y) - 36 * sy;
                        if (left < -0.6 || top < -0.6 || left + pw * sx > image.Width + 0.6 || top + ph * sy > image.Height + 0.6) continue;
                        int bx = Math.Max(0, (int)Math.Floor(left)), by = Math.Max(0, (int)Math.Floor(top));
                        var bounds = new FrameBounds(bx, by, Math.Min(image.Width, (int)Math.Ceiling(left + pw * sx)) - bx,
                            Math.Min(image.Height, (int)Math.Ceiling(top + ph * sy)) - by);
                        if (nativeBounds.Contains(bounds)) continue;
                        var restored = TrySample(image, size, turns, left, top, sx, sy, out bool probe);
                        if (probe) status = FrameReadStatus.Calibration;
                        if (restored == null) continue;
                        packets.Add(new(restored, bounds, (sx + sy) / 2));
                        nativeBounds.Add(bounds);
                        break;
                    }
                }
        return new(packets, packets.Count > 0 ? FrameReadStatus.Success : status);
    }

    // A header gate only bounds work; Parse still verifies the original CRC before
    // accepting any packet, and CryptoEnvelope still authenticates the assembled file.
    private static int PacketByteCount(ReadOnlySpan<byte> header, FrameSize size)
    {
        if (header.Length < 36 || !header[..4].SequenceEqual("PCF1"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[32..]) != size.Width ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[34..]) != size.Height) return 0;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);
        int index = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
        int total = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
        int capacity = size.PayloadBytes;
        if (length < 1 || length > capacity || total < 1 || index < 0 || index >= total ||
            total > (CryptoEnvelope.MaxEnvelopeBytes + capacity - 1) / capacity ||
            (index < total - 1 && length != capacity)) return 0;
        return length + TransferPacket.Overhead;
    }

    private static bool IsProbeHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 36) return false;
        uint state = 0x50435431;
        for (int i = 0; i < 36; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            if (bytes[i] != (byte)state) return false;
        }
        return true;
    }

    private static TransferPacket? TrySample(RgbImage image, FrameSize size, int turns,
        double left, double top, double sx, double sy, out bool probe)
    {
        probe = false;
        // Small phase offsets account for rounding at the finder edges. No byte correction:
        // only a packet whose original CRC validates can be returned.
        double[] phases = [0, 0.25, -0.25, 0.5, -0.5];
        byte[] header = new byte[36];
        var integerPhases = new HashSet<(int X, int Y)>();
        bool integerScale = sx == Math.Round(sx) && sy == Math.Round(sy);
        byte[]? buffer = null;
        foreach (double ox in phases)
            foreach (double oy in phases)
            {
                if (integerScale && !integerPhases.Add(((int)Math.Floor(left + sx * 0.5 + ox), (int)Math.Floor(top + sy * 0.5 + oy)))) continue;
                Sample(header, ox, oy);
                if (IsProbeHeader(header)) { probe = true; return null; }
                int byteCount = PacketByteCount(header, size);
                if (byteCount == 0) continue;
                byte[] bytes = buffer != null && buffer.Length == byteCount
                    ? buffer : buffer = new byte[byteCount];
                Sample(bytes, ox, oy);
                var packet = TransferPacket.Parse(bytes, size);
                if (packet != null) return packet;
            }
        return null;

        void Sample(byte[] bytes, double ox, double oy)
        {
            int pw = turns % 2 == 0 ? size.Width : size.Height, ph = turns % 2 == 0 ? size.Height : size.Width;
            for (int p = 0; p < (bytes.Length + 2) / 3; p++)
            {
                double x = 64 + p % size.DataWidth + 0.5, y = 104 + p / size.DataWidth + 0.5;
                var (u, v) = turns switch
                {
                    1 => (pw - y, x), 2 => (pw - x, ph - y), 3 => (y, ph - x), _ => (x, y)
                };
                int ix = Math.Clamp((int)Math.Floor(left + u * sx + ox), 0, image.Width - 1);
                int iy = Math.Clamp((int)Math.Floor(top + v * sy + oy), 0, image.Height - 1);
                int src = (iy * image.Width + ix) * 3;
                for (int c = 0; c < 3 && p * 3 + c < bytes.Length; c++) bytes[p * 3 + c] = image.Pixels[src + c];
            }
        }
    }
}

