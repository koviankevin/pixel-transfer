using PixelTransfer.Core;
using System.Buffers.Binary;

int passed = 0;
void Assert(bool ok, string message = "Assertion failed") { if (!ok) throw new Exception(message); }
void Reject(Action action) { try { action(); } catch (Exception) { return; } throw new Exception("Expected rejection"); }
void Test(string name, Action action) { action(); passed++; Console.WriteLine($"PASS {name}"); }
byte[] RandomData(int size) { byte[] data = new byte[size]; new Random(152).NextBytes(data); return data; }
var password = "測試-password";
foreach (var data in new[] { Array.Empty<byte>(), new byte[50000], RandomData(40000) })
    Test($"Authenticated round trip {data.Length}", () => {
        var encrypted = CryptoEnvelope.Encrypt(data, "測試.zip", password);
        var result = CryptoEnvelope.Decrypt(encrypted, password);
        Assert(result.Name == "測試.zip" && result.Data.SequenceEqual(data));
    });
var source = RandomData(4_000_000);
var envelope = CryptoEnvelope.Encrypt(source, "example.zip", password);
Test("Randomized encryption", () => Assert(!CryptoEnvelope.Encrypt(source, "example.zip", password).SequenceEqual(envelope)));
Test("Wrong password and modified ciphertext", () => {
    Reject(() => CryptoEnvelope.Decrypt(envelope, "wrong"));
    var changed = (byte[])envelope.Clone(); changed[^1] ^= 1;
    Reject(() => CryptoEnvelope.Decrypt(changed, password));
});
Test("Safe output filename", () => Assert(CryptoEnvelope.SafeName("../../a\\file.zip") == "file.zip"));
foreach (var size in new[] { new FrameSize(640, 360), new FrameSize(1280, 720), new FrameSize(1600, 900), new FrameSize(1920, 1080) })
    Test($"Frames and reversed duplicates {size}", () => {
        var collector = new TransferCollector();
        foreach (var packet in TransferPacket.Split(envelope, size).Reverse())
        {
            var decoded = FrameCodec.Read(FrameCodec.Render(packet));
            Assert(decoded != null); collector.Add(decoded!); collector.Add(decoded!);
        }
        Assert(collector.Complete);
        Assert(CryptoEnvelope.Decrypt(collector.Assemble(), password).Data.SequenceEqual(source));
    });
var smallEnvelope = CryptoEnvelope.Encrypt(RandomData(10000), "a.bin", password);
var sample = TransferPacket.Split(smallEnvelope, new(1280, 720))[0];
for (int turn = 0; turn < 4; turn++)
    Test($"Quarter turn {turn}", () => {
        var rotated = FrameCodec.Render(sample).Rotate(turn);
        var background = new RgbImage(rotated.Width + 100, rotated.Height + 120);
        Array.Fill(background.Pixels, (byte)100);
        for (int y = 0; y < rotated.Height; y++) Buffer.BlockCopy(rotated.Pixels, y * rotated.Width * 3, background.Pixels, ((y + 53) * background.Width + 37) * 3, rotated.Width * 3);
        Assert(FrameCodec.Read(background)?.Payload.SequenceEqual(sample.Payload) == true);
        var tracker = new FrameTracker();
        Assert(tracker.Read(_ => background, new(-100, -200, background.Width, background.Height)) != null);
        Assert(tracker.Bounds == new FrameBounds(-63, -147, rotated.Width, rotated.Height));
    });
Test("Missing marker rejected", () => {
    var image = FrameCodec.Render(sample); image.FillRect(0, 0, 64, 64, 255, 255, 255);
    Assert(FrameCodec.Read(image) == null);
});
Test("Single channel damage rejected", () => {
    var image = FrameCodec.Render(sample); image.Pixels[(104 * image.Width + 100) * 3] ^= 1;
    Assert(FrameCodec.Read(image) == null);
});
Test("Probe exact and one changed channel", () => {
    var image = FrameCodec.Probe(new(1280, 720)); Assert(FrameCodec.Measure(image)?.Passed == true);
    image.Pixels[(200 * image.Width + 200) * 3] ^= 1;
    var result = FrameCodec.Measure(image.Rotate(1));
    Assert(result is { ChangedChannels: 1, ChangedPixels: 1, MaxError: 1 });
});
Test("Missing and mixed transfer rejected", () => {
    var packets = TransferPacket.Split(envelope, new(1280, 720));
    var collector = new TransferCollector(); collector.Add(packets[0]);
    Reject(() => collector.Assemble());
    Reject(() => collector.Add(sample));
});
Test("Malicious header bounds rejected", () => {
    byte[] raw = sample.Serialize(); BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(24), int.MaxValue);
    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(raw.Length - 4), TransferPacket.Crc32(raw.AsSpan(0, raw.Length - 4)));
    Assert(TransferPacket.Parse(raw, sample.Size) == null);
});
Test("Upscaled pixels restored", () => {
    var image = FrameCodec.Render(sample); var scaled = new RgbImage(image.Width * 2, image.Height * 2);
    for (int y = 0; y < scaled.Height; y++) for (int x = 0; x < scaled.Width; x++)
        Buffer.BlockCopy(image.Pixels, ((y / 2) * image.Width + x / 2) * 3, scaled.Pixels, (y * scaled.Width + x) * 3, 3);
    Assert(FrameCodec.Read(scaled)?.Payload.SequenceEqual(sample.Payload) == true);
});
var desktop = new FrameBounds(-100, 30, 1500, 900);
var smallPacket = TransferPacket.Split(smallEnvelope, new(640, 360))[0];
var otherPacket = TransferPacket.Split(envelope, new(640, 360))[0];
RgbImage Scene(params (TransferPacket Packet, int X, int Y)[] frames)
{
    var result = new RgbImage(desktop.Width, desktop.Height);
    Array.Fill(result.Pixels, (byte)100);
    foreach (var (packet, x, y) in frames)
    {
        var frame = FrameCodec.Render(packet);
        for (int row = 0; row < frame.Height; row++)
            Buffer.BlockCopy(frame.Pixels, row * frame.Width * 3, result.Pixels, ((row + y) * result.Width + x) * 3, frame.Width * 3);
    }
    return result;
}
RgbImage screen = Scene((smallPacket, 40, 70));
var captures = new List<FrameBounds>();
RgbImage Capture(FrameBounds bounds)
{
    captures.Add(bounds);
    var result = new RgbImage(bounds.Width, bounds.Height);
    for (int row = 0; row < bounds.Height; row++)
        Buffer.BlockCopy(screen.Pixels, ((row + bounds.Y - desktop.Y) * screen.Width + bounds.X - desktop.X) * 3,
            result.Pixels, row * bounds.Width * 3, bounds.Width * 3);
    return result;
}
Test("Auto receiver finds, crops, and reacquires moved frame", () => {
    var tracker = new FrameTracker();
    Assert(tracker.Read(Capture, desktop) != null && captures[^1] == desktop);
    Assert(tracker.Bounds == new FrameBounds(-60, 100, 640, 360));
    captures.Clear();
    Assert(tracker.Read(Capture, desktop) != null && captures.Count == 1 && captures[0] == tracker.Bounds);
    screen = Scene((smallPacket, 710, 400)); captures.Clear();
    Assert(tracker.Read(Capture, desktop) != null && captures.Count == 2 && captures[^1] == desktop);
    Assert(tracker.Bounds == new FrameBounds(610, 430, 640, 360));
});
Test("Auto receiver preserves identity across disappearance and ignores other transfers", () => {
    screen = Scene((smallPacket, 40, 70)); var tracker = new FrameTracker();
    Assert(tracker.Read(Capture, desktop) != null);
    screen = Scene(); Assert(tracker.Read(Capture, desktop) == null && tracker.Bounds == null);
    screen = Scene((otherPacket, 40, 70)); Assert(tracker.Read(Capture, desktop) == null);
    screen = Scene((otherPacket, 40, 70), (smallPacket, 760, 450));
    Assert(tracker.Read(Capture, desktop)?.TransferId.SequenceEqual(smallPacket.TransferId) == true);
});
Test("Auto receiver rejects ambiguous initial screen without locking", () => {
    var tracker = new FrameTracker(); screen = Scene((smallPacket, 40, 70), (otherPacket, 760, 450));
    Reject(() => tracker.Read(Capture, desktop)); Assert(tracker.Bounds == null);
    screen = Scene((otherPacket, 40, 70));
    Assert(tracker.Read(Capture, desktop)?.TransferId.SequenceEqual(otherPacket.TransferId) == true);
});
Test("Auto receiver rejects damaged packet before locking", () => {
    var tracker = new FrameTracker(); screen = Scene((smallPacket, 40, 70));
    screen.Pixels[((70 + 104) * screen.Width + 40 + 100) * 3] ^= 1;
    Assert(tracker.Read(Capture, desktop) == null && tracker.Bounds == null);
    screen = Scene((otherPacket, 40, 70)); Assert(tracker.Read(Capture, desktop) != null);
});
RgbImage Resize(RgbImage image, double scale, bool bilinear = false)
{
    var resized = new RgbImage((int)Math.Round(image.Width * scale), (int)Math.Round(image.Height * scale));
    for (int y = 0; y < resized.Height; y++)
        for (int x = 0; x < resized.Width; x++)
        {
            double sx = (x + 0.5) * image.Width / resized.Width - 0.5;
            double sy = (y + 0.5) * image.Height / resized.Height - 0.5;
            for (int c = 0; c < 3; c++)
            {
                byte At(int ix, int iy) => image.Pixels[(Math.Clamp(iy, 0, image.Height - 1) * image.Width + Math.Clamp(ix, 0, image.Width - 1)) * 3 + c];
                double value;
                if (!bilinear) value = At((int)Math.Floor(sx + 0.5), (int)Math.Floor(sy + 0.5));
                else
                {
                    int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy);
                    double fx = sx - ix, fy = sy - iy;
                    value = (At(ix, iy) * (1 - fx) + At(ix + 1, iy) * fx) * (1 - fy) +
                        (At(ix, iy + 1) * (1 - fx) + At(ix + 1, iy + 1) * fx) * fy;
                }
                resized.Pixels[(y * resized.Width + x) * 3 + c] = (byte)Math.Round(value);
            }
        }
    return resized;
}
// Fill every row with non-compressible content, so scale recovery cannot pass on a short header alone.
var densePacket = TransferPacket.Split(envelope, new(640, 360))[0];
foreach (double scale in new[] { 1.25, 1.5, 1.75, 2.0, 3.0, 4.0 })
    Test($"Nearest-neighbor {scale}x restores every payload byte", () => {
        var inspection = FrameCodec.Inspect(Resize(FrameCodec.Render(densePacket), scale));
        Assert(inspection.Status == FrameReadStatus.Success, inspection.Message);
        Assert(inspection.Packets[0].Packet.Payload.SequenceEqual(densePacket.Payload));
        Assert(Math.Abs(inspection.Packets[0].Scale - scale) < 0.001);
    });
for (int turn = 0; turn < 4; turn++)
    Test($"Scaled finder rotation {turn} with desktop offset", () => {
        var resized = Resize(FrameCodec.Render(densePacket), 1.5).Rotate(turn);
        var background = new RgbImage(resized.Width + 100, resized.Height + 120);
        Array.Fill(background.Pixels, (byte)100);
        for (int row = 0; row < resized.Height; row++)
            Buffer.BlockCopy(resized.Pixels, row * resized.Width * 3, background.Pixels, ((row + 53) * background.Width + 37) * 3, resized.Width * 3);
        var inspection = FrameCodec.Inspect(background);
        Assert(inspection.Packets.FirstOrDefault()?.Packet.Payload.SequenceEqual(densePacket.Payload) == true);
        Assert(inspection.Packets[0].Bounds == new FrameBounds(37, 53, resized.Width, resized.Height));
    });
Test("Bilinear 3x can retain exact sample centers", () => {
    var inspection = FrameCodec.Inspect(Resize(FrameCodec.Render(densePacket), 3, true));
    Assert(inspection.Packets.FirstOrDefault()?.Packet.Payload.SequenceEqual(densePacket.Payload) == true, inspection.Message);
});
foreach (var (scale, bilinear) in new[] { (0.5, false), (0.75, false), (0.75, true), (1.5, true) })
    Test($"Lossy resize {scale}x bilinear={bilinear} detected but rejected", () => {
        var inspection = FrameCodec.Inspect(Resize(FrameCodec.Render(densePacket), scale, bilinear));
        Assert(inspection.Status == FrameReadStatus.InvalidData && inspection.Packets.Count == 0, inspection.Message);
    });
Test("Scaled frame with damaged payload is never accepted", () => {
    var frame = FrameCodec.Render(densePacket); frame.Pixels[(200 * frame.Width + 300) * 3] ^= 1;
    var inspection = FrameCodec.Inspect(Resize(frame, 2));
    Assert(inspection.Status == FrameReadStatus.InvalidData && inspection.Packets.Count == 0);
});
Test("Diagnostics distinguish blank, partial, corrupted and calibration images", () => {
    Assert(FrameCodec.Inspect(new RgbImage(640, 360)).Status == FrameReadStatus.NoMarkers);
    var partial = FrameCodec.Render(densePacket); partial.FillRect(0, 0, 64, 64, 255, 255, 255);
    Assert(FrameCodec.Inspect(partial).Status == FrameReadStatus.IncompleteMarkers);
    var damaged = FrameCodec.Render(densePacket); damaged.Pixels[(200 * damaged.Width + 300) * 3] ^= 1;
    Assert(FrameCodec.Inspect(damaged).Status == FrameReadStatus.InvalidData);
    Assert(FrameCodec.Inspect(FrameCodec.Probe(new(640, 360))).Status == FrameReadStatus.Calibration);
    Assert(FrameCodec.Inspect(Resize(FrameCodec.Probe(new(640, 360)), 1.5)).Status == FrameReadStatus.Calibration);
});
Test("Tracker crops a scaled frame and reports failed restoration", () => {
    var tracker = new FrameTracker();
    var resized = Resize(FrameCodec.Render(densePacket), 1.5);
    var background = new RgbImage(1200, 700);
    Array.Fill(background.Pixels, (byte)100);
    for (int row = 0; row < resized.Height; row++)
        Buffer.BlockCopy(resized.Pixels, row * resized.Width * 3, background.Pixels, ((row + 53) * background.Width + 37) * 3, resized.Width * 3);
    var search = new FrameBounds(0, 0, background.Width, background.Height);
    Assert(tracker.Read(_ => background, search) != null);
    Assert(tracker.Bounds == new FrameBounds(37, 53, resized.Width, resized.Height));
    int captures = 0;
    Assert(tracker.Read(b => { captures++; Assert(b == tracker.Bounds); return resized; }, search) != null);
    Assert(captures == 1 && tracker.Scale == 1.5);
    var lost = Resize(FrameCodec.Render(densePacket), 0.5);
    Assert(tracker.Read(_ => lost, search) == null && tracker.Inspection.Status == FrameReadStatus.InvalidData);
});
passed += StressRegression.Run();
passed += DiagnosticRegression.Run();
Console.WriteLine($"{passed} tests passed.");
