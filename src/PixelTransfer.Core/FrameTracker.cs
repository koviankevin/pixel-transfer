using System.Diagnostics;

namespace PixelTransfer.Core;

// Retains decoded packets and transfer geometry, never a desktop screenshot.
public sealed class FrameTracker
{
    private byte[]? transferId;
    private bool everLocked;
    public long TrackingLosses { get; private set; }
    public long Reacquisitions { get; private set; }
    public long FullSearches { get; private set; }
    public Action<FrameInspection, FrameBounds, double, bool>? InspectionObserved { get; set; }

    private TransferPacket? CaptureAndSearch(Func<FrameBounds, RgbImage> capture, FrameBounds area, bool fullSearch)
    {
        long start = Stopwatch.GetTimestamp();
        var packet = Search(capture(area), area);
        InspectionObserved?.Invoke(Inspection, area, Stopwatch.GetElapsedTime(start).TotalMilliseconds, fullSearch);
        return packet;
    }
    public FrameBounds? Bounds { get; private set; }
    public FrameInspection Inspection { get; private set; } = new([], FrameReadStatus.NoMarkers);
    public double Scale { get; private set; } = 1;

    public TransferPacket? Read(Func<FrameBounds, RgbImage> capture, FrameBounds desktop)
    {
        if (Bounds is { } area && area.X >= desktop.X && area.Y >= desktop.Y &&
            (long)area.X + area.Width <= (long)desktop.X + desktop.Width &&
            (long)area.Y + area.Height <= (long)desktop.Y + desktop.Height)
        {
            var tracked = CaptureAndSearch(capture, area, false);
            if (tracked != null) return tracked;
            // A tracked frame can occupy the entire desktop. Its failed capture was
            // already a full search; do not capture and inspect the same area twice.
            if (area == desktop) { TrackingLosses++; Bounds = null; return null; }
        }
        if (Bounds != null) TrackingLosses++;
        Bounds = null;
        FullSearches++;
        var found = CaptureAndSearch(capture, desktop, true);
        if (found != null)
        {
            if (everLocked) Reacquisitions++;
            everLocked = true;
        }
        return found;
    }

    private TransferPacket? Search(RgbImage image, FrameBounds origin)
    {
        TransferPacket? match = null;
        FrameBounds? bounds = null;
        Inspection = FrameCodec.Inspect(image);
        foreach (var frame in Inspection.Packets)
        {
            var packet = frame.Packet;
            if (transferId != null && !transferId.AsSpan().SequenceEqual(packet.TransferId)) continue;
            if (match != null && !match.TransferId.AsSpan().SequenceEqual(packet.TransferId))
                throw new InvalidOperationException("畫面有多個不同傳輸，請只保留一個播放視窗，或改用手動框選。");
            match = packet;
            bounds = frame.Bounds with { X = origin.X + frame.Bounds.X, Y = origin.Y + frame.Bounds.Y };
            Scale = frame.Scale;
        }
        if (match != null)
        {
            transferId ??= (byte[])match.TransferId.Clone();
            Bounds = bounds;
        }
        return match;
    }
}
