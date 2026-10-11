using PixelTransfer.Core;
using System.Buffers.Binary;

internal static class StressRegression
{
    internal static int Run()
    {
        int passed = 0;
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        void Test(string name, Action action) { action(); passed++; Console.WriteLine($"PASS {name}"); }
        void Reject(RgbImage image) => Assert(FrameCodec.Inspect(image).Packets.Count == 0, "False packet acceptance");
        var packet = SyntheticScenes.Packet();
        foreach (int count in new[] { 3, 12, 24 })
            Test($"Complex desktop with {count} aligned decoys rejects data", () => {
                var image = SyntheticScenes.Grid(1920, 1080, count);
                Reject(image);
            });
        Test("4K 24-marker search has bounded allocations", () => {
            var image = SyntheticScenes.Grid(3840, 2160, 24);
            // Warm JIT/static state, then measure only Inspect. The old eager native
            // extraction allocates >1 GB here. Leave headroom for normal scratch work.
            Reject(image);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var result = FrameCodec.Inspect(image);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(result.Packets.Count == 0 && result.Status == FrameReadStatus.InvalidData, "Decoys bypassed geometry/data checks");
            Assert(allocated < image.Width * (long)image.Height + 8_000_000, $"Candidate payload allocation regression: {allocated} bytes");
        });
        Test("24 scaled decoys reject data", () => Reject(SyntheticScenes.Grid(3840, 2160, 24, 60)));
        Test("Above marker limit remains fail-closed", () => {
            var image = SyntheticScenes.Desktop(1920, 1080);
            for (int i = 0; i < 25; i++) SyntheticScenes.Marker(image, 80 + i % 5 * 350, 80 + i / 5 * 210);
            Reject(image); // Existing cap deliberately does not attempt a partial candidate set.
        });
        Test("Real packet survives 21 decoys without duplicate acceptance", () => {
            var result = FrameCodec.Inspect(SyntheticScenes.WithPacket(packet));
            Assert(result.Packets.Count == 1 && result.Packets[0].Packet.TransferId.SequenceEqual(packet.TransferId),
                "Missing/duplicate packet");
            Assert(result.Packets[0].Packet.Payload.SequenceEqual(packet.Payload), "Payload mismatch");
        });
        Test("Corruption with 21 decoys is rejected", () => {
            var image = SyntheticScenes.WithPacket(packet);
            image.Pixels[((850 + 104) * image.Width + 1300 + 100) * 3] ^= 1;
            Reject(image);
        });
        Test("Plausible magic with hostile lengths cannot allocate payload", () => {
            foreach (int length in new[] { -1, 0, int.MaxValue, packet.Size.PayloadBytes + 1 })
            {
                byte[] raw = packet.Serialize();
                BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(28), length);
                var image = FrameCodec.RenderBytes(raw, packet.Size);
                Reject(image);
            }
        });
        Test("Native custom sizes, rotations and partial RGB tails still decode", () => {
            foreach (var size in new[] { new FrameSize(647, 373), new FrameSize(640, 360) })
                foreach (int length in new[] { 1, 2, 3, 8191 })
                    for (int turn = 0; turn < 4; turn++)
                    {
                        var custom = packet with { Size = size, Payload = packet.Payload.Take(length).ToArray() };
                        var result = FrameCodec.Read(FrameCodec.Render(custom).Rotate(turn));
                        Assert(result?.Payload.SequenceEqual(custom.Payload) == true, "Native tail/rotation mismatch");
                    }
        });
        Test("Full-desktop tracking loss does not capture the same area twice", () => {
            var frame = FrameCodec.Render(packet);
            var area = new FrameBounds(-640, 20, frame.Width, frame.Height);
            var tracker = new FrameTracker();
            Assert(tracker.Read(_ => frame, area) != null, "Initial lock failed");
            int captures = 0;
            var blank = SyntheticScenes.Desktop(frame.Width, frame.Height);
            Assert(tracker.Read(_ => { captures++; return blank; }, area) == null, "Blank accepted");
            Assert(captures == 1 && tracker.Bounds == null, $"Redundant full-desktop searches: {captures}");
            Assert(tracker.Read(_ => frame, area) != null, "Could not reacquire after loss");
        });
        Test("Continuous tracking samples fresh payload and reacquires amid decoys", () => {
            var tracker = new FrameTracker();
            var screen = SyntheticScenes.WithPacket(packet);
            var desktop = new FrameBounds(0, 0, screen.Width, screen.Height);
            int captures = 0;
            TransferPacket? Read() => tracker.Read(b => { captures++; return SyntheticScenes.Crop(screen, b); }, desktop);
            Assert(Read() != null, "Initial search failed");
            for (int i = 0; i < 12; i++)
            {
                var next = packet with { Payload = (byte[])packet.Payload.Clone() };
                next.Payload[0] = (byte)i;
                screen = SyntheticScenes.WithPacket(next);
                captures = 0;
                Assert(Read()?.Payload.SequenceEqual(next.Payload) == true && captures == 1, "Stale payload or redundant capture");
            }
            screen = SyntheticScenes.Grid(2560, 1440, 24); captures = 0;
            Assert(Read() == null && captures == 2, "Loss must fall back to desktop once");
            for (int i = 0; i < 32; i++) { captures = 0; Assert(Read() == null && captures == 1, "Repeated miss recaptured desktop"); }
            screen = SyntheticScenes.WithPacket(packet, 1700, 1000);
            Assert(Read()?.Payload.SequenceEqual(packet.Payload) == true, "Moved packet not reacquired");
            Assert(tracker.Bounds == new FrameBounds(1700, 1000, 640, 360), "Wrong reacquired bounds");
        });
        Test("CRC-valid modified ciphertext still fails AES-GCM after image decode", () => {
            byte[] encrypted = CryptoEnvelope.Encrypt(new byte[500], "synthetic.bin", "test-only");
            encrypted[32] ^= 1; // first ciphertext byte, past the authenticated envelope header
            var transfer = TransferPacket.Split(encrypted, new(640, 360));
            var collector = new TransferCollector();
            foreach (var part in transfer)
            {
                var decoded = FrameCodec.Read(FrameCodec.Render(part));
                Assert(decoded != null, "CRC-valid synthetic packet should decode");
                collector.Add(decoded!);
            }
            bool rejected = false;
            try { CryptoEnvelope.Decrypt(collector.Assemble(), "test-only"); }
            catch (InvalidDataException ex) when (ex.InnerException is System.Security.Cryptography.AuthenticationTagMismatchException) { rejected = true; }
            Assert(rejected, "Modified authenticated envelope was accepted");
        });
        return passed;
    }
}
