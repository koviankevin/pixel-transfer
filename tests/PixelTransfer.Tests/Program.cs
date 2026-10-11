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
Test("Upscaled pixels rejected", () => {
    var image = FrameCodec.Render(sample); var scaled = new RgbImage(image.Width * 2, image.Height * 2);
    for (int y = 0; y < scaled.Height; y++) for (int x = 0; x < scaled.Width; x++)
        Buffer.BlockCopy(image.Pixels, ((y / 2) * image.Width + x / 2) * 3, scaled.Pixels, (y * scaled.Width + x) * 3, 3);
    Assert(FrameCodec.Read(scaled) == null);
});
Console.WriteLine($"{passed} tests passed.");
