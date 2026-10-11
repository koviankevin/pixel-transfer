namespace PixelTransfer.Core;

// Retains only the transfer identity and rectangle, never a desktop screenshot.
public sealed class FrameTracker
{
    private byte[]? transferId;
    public FrameBounds? Bounds { get; private set; }

    public TransferPacket? Read(Func<FrameBounds, RgbImage> capture, FrameBounds desktop)
    {
        if (Bounds is { } area && area.X >= desktop.X && area.Y >= desktop.Y &&
            (long)area.X + area.Width <= (long)desktop.X + desktop.Width &&
            (long)area.Y + area.Height <= (long)desktop.Y + desktop.Height)
        {
            var tracked = Search(capture(area), area);
            if (tracked != null) return tracked;
        }
        Bounds = null;
        return Search(capture(desktop), desktop);
    }

    private TransferPacket? Search(RgbImage image, FrameBounds origin)
    {
        TransferPacket? match = null;
        FrameBounds? bounds = null;
        foreach (var frame in FrameCodec.Locate(image))
        {
            var packet = TransferPacket.Parse(frame.Bytes, frame.Size);
            if (packet == null || (transferId != null && !transferId.AsSpan().SequenceEqual(packet.TransferId))) continue;
            if (match != null && !match.TransferId.AsSpan().SequenceEqual(packet.TransferId))
                throw new InvalidOperationException("畫面有多個不同傳輸，請只保留一個播放視窗，或改用手動框選。");
            match = packet;
            bounds = frame.Bounds with { X = origin.X + frame.Bounds.X, Y = origin.Y + frame.Bounds.Y };
        }
        if (match != null)
        {
            transferId ??= (byte[])match.TransferId.Clone();
            Bounds = bounds;
        }
        return match;
    }
}
