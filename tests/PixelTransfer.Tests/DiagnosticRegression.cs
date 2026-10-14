using System.Buffers.Binary;
using System.Text.Json;
using PixelTransfer.Core;

internal static class DiagnosticRegression
{
    private sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Milliseconds);
    }
    internal static int Run()
    {
        int passed = 0;
        void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
        void Test(string name, Action test) { test(); passed++; Console.WriteLine($"PASS {name}"); }
        var size = new FrameSize(640, 360);
        var packet = TransferPacket.Split(System.Text.Encoding.UTF8.GetBytes("PRIVATE-PAYLOAD-DO-NOT-EXPORT"), size)[0];
        var display = new DisplayMetrics(1920, 1080, 144, 144);
        Test("Diagnostics distinguish CRC damage from malformed header and missing geometry", () =>
        {
            var valid = FrameCodec.Inspect(FrameCodec.Render(packet));
            Assert(valid.HasFrameGeometry && !valid.CrcFailureObserved && valid.Packets.Count == 1, "Valid decode changed");
            byte[] raw = packet.Serialize(); raw[36] ^= 1;
            var damaged = FrameCodec.Inspect(FrameCodec.RenderBytes(raw, size));
            Assert(damaged.HasFrameGeometry && damaged.CrcFailureObserved && damaged.Packets.Count == 0, "CRC not counted");
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(28), int.MaxValue);
            var header = FrameCodec.Inspect(FrameCodec.RenderBytes(raw, size));
            Assert(header.HasFrameGeometry && !header.CrcFailureObserved, "Invalid header mislabeled CRC");
            var blank = FrameCodec.Inspect(new RgbImage(640, 360));
            Assert(!blank.HasFrameGeometry && !blank.CrcFailureObserved, "Blank mislabeled");
            var session = new DiagnosticSession(DiagnosticMode.SyntheticRegression);
            foreach (var result in new[] { valid, damaged, header, blank })
                session.RecordInspection(result, display, 640, 360, 2, CaptureKind.ManualRegion);
            var summary = session.Snapshot().Summary;
            Assert(summary is { Samples: 4, LocatedSamples: 3, ValidPacketSamples: 1, CrcFailureSamples: 1 }
                && summary.LocalizationRate == 0.75, "Per-capture accounting incorrect");
        });
        Test("Tracker diagnostics count loss and same-cycle moved-window reacquisition", () =>
        {
            var screen = SyntheticScenes.WithPacket(packet, 100, 100);
            var desktop = new FrameBounds(0, 0, screen.Width, screen.Height);
            var session = new DiagnosticSession(DiagnosticMode.SyntheticRegression);
            var tracker = new FrameTracker
            {
                InspectionObserved = (i, b, ms, full) => session.RecordInspection(i, display, b.Width, b.Height, ms,
                    full ? CaptureKind.FullSearch : CaptureKind.TrackedRegion)
            };
            TransferPacket? Read() => tracker.Read(b => SyntheticScenes.Crop(screen, b), desktop);
            Assert(Read() != null && tracker.Reacquisitions == 0 && tracker.FullSearches == 1, "Initial lock is not reacquisition");
            Assert(Read() != null && tracker.FullSearches == 1, "Tracked read unnecessarily searched desktop");
            screen = SyntheticScenes.WithPacket(packet, 1300, 850);
            Assert(Read() != null && tracker.Reacquisitions == 1 && tracker.TrackingLosses == 1, "Same-cycle reacquisition missing");
            Assert(session.Snapshot().Summary.Samples == 4, "Lost ROI and fallback must both be counted");
            screen = SyntheticScenes.Desktop(desktop.Width, desktop.Height);
            Assert(Read() == null && tracker.TrackingLosses == 2, "Loss missing");
            Assert(Read() == null && tracker.TrackingLosses == 2, "Repeated miss inflated losses");
            screen = SyntheticScenes.WithPacket(packet, 1300, 850);
            Assert(Read() != null && tracker.Reacquisitions == 2, "Recovery missing");
            session.RecordReceiveCycle(true, 1, 1, tracker.TrackingLosses, tracker.Reacquisitions, tracker.FullSearches);
            Assert(session.Snapshot().Summary.Reacquisitions == 2, "Report lost tracker counters");
        });
        Test("Full-desktop loss is counted once and different transfer is not reacquired", () =>
        {
            var tracker = new FrameTracker();
            var bounds = new FrameBounds(0, 0, 640, 360);
            tracker.Read(_ => FrameCodec.Render(packet), bounds);
            Assert(tracker.Read(_ => new RgbImage(640, 360), bounds) == null && tracker.TrackingLosses == 1, "Desktop loss missing");
            var other = TransferPacket.Split(new byte[123], size)[0];
            Assert(tracker.Read(_ => FrameCodec.Render(other), bounds) == null && tracker.Reacquisitions == 0, "Foreign transfer accepted");
            Assert(tracker.Read(_ => FrameCodec.Render(packet), bounds) != null && tracker.Reacquisitions == 1, "Original did not recover");
        });
        Test("Probe pass failure and not-located have separate scalar results", () =>
        {
            var session = new DiagnosticSession(DiagnosticMode.SyntheticRegression);
            var image = FrameCodec.Probe(size);
            session.RecordProbe(FrameCodec.Measure(image), display, 640, 360, 10);
            image.Pixels[(104 * image.Width + 100) * 3] ^= 1;
            session.RecordProbe(FrameCodec.Measure(image), display, 640, 360, 20);
            session.RecordProbe(null, new(2560, 1440, 192, 192), 800, 450, 30);
            var report = session.Snapshot();
            Assert(report.Summary is { ProbeSamples: 3, ProbePasses: 1, LocatedSamples: 2, CrcFailureSamples: 0 }, "Probe treated as packet CRC");
            Assert(report.RecentSamples[1].ChangedChannels == 1 && report.RecentSamples[2].Display.DpiX == 192, "Lost pixel error or DPI change");
            Assert(report.Summary.MeanCaptureAndInspectMs == 20 && report.Summary.MaxCaptureAndInspectMs == 30, "Timing aggregation wrong");
        });
        Test("Duration uses monotonic time and duplicate cycles are distinct from unique packets", () =>
        {
            var clock = new Clock(); var session = new DiagnosticSession(DiagnosticMode.SyntheticRegression, clock);
            clock.Milliseconds = 100; session.RecordReceiveCycle(false, 0, 0);
            clock.Milliseconds = 250; session.RecordReceiveCycle(true, 1, 2);
            clock.Milliseconds = 500; session.RecordReceiveCycle(true, 1, 2);
            clock.Milliseconds = 750; session.RecordReceiveCycle(true, 2, 2);
            session.Finish(DiagnosticEnd.Completed); clock.Milliseconds = 5000;
            session.Finish(DiagnosticEnd.Error); session.RecordReceiveCycle(true, 2, 2);
            var r = session.Snapshot();
            Assert(r.ElapsedMs == 750 && r.End == DiagnosticEnd.Completed && r.EndedUtc == DateTimeOffset.UnixEpoch.AddMilliseconds(750), "Finish was not stable");
            Assert(r.Summary is { ReceiveCycles: 4, AcceptedCycles: 3, DuplicateCycles: 1, UniquePackets: 2, FirstAcceptedMs: 250 }, "Receive counters incorrect");
        });
        Test("Long sessions retain bounded details and complete counters", () =>
        {
            var session = new DiagnosticSession(DiagnosticMode.SyntheticRegression);
            for (int i = 0; i < 10000; i++) session.RecordProbe(null, display, 640, 360, 1);
            var r = session.Snapshot();
            Assert(r.Summary.Samples == 10000 && r.Summary.DroppedDetails == 10000 - DiagnosticSession.DetailLimit
                && r.RecentSamples.Count == DiagnosticSession.DetailLimit && r.RecentSamples[^1].Number == 10000, "Unbounded or truncated totals");
        });
        Test("JSON exports only allowlisted scalars and never claims real RDP verification", () =>
        {
            var session = new DiagnosticSession(DiagnosticMode.SyntheticRegression);
            session.RecordInspection(FrameCodec.Inspect(FrameCodec.Render(packet)), display, 640, 360, 3, CaptureKind.ManualRegion);
            session.Finish(DiagnosticEnd.Completed);
            string json = session.ToJson();
            foreach (string secret in new[] { "PRIVATE-PAYLOAD-DO-NOT-EXPORT", Convert.ToBase64String(packet.Payload), Convert.ToBase64String(packet.TransferId) })
                Assert(!json.Contains(secret), "Sensitive data escaped");
            using var doc = JsonDocument.Parse(json);
            Assert(doc.RootElement.GetProperty("RealRdpValidation").GetString() == "not-verified", "Synthetic result claimed RDP validation");
            var sample = doc.RootElement.GetProperty("RecentSamples")[0];
            string[] allowed = ["Number", "ElapsedMs", "Kind", "Display", "CaptureWidth", "CaptureHeight", "CaptureAndInspectMs", "Located", "ValidPacket", "CrcFailureObserved", "ProbePassed", "FrameWidth", "FrameHeight", "Scale", "ChangedChannels"];
            Assert(sample.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(allowed.Order()), "Export schema expanded without privacy review");
            Assert(sample.GetProperty("Display").EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "Width", "Height", "DpiX", "DpiY" }.Order()), "Display identifiers leaked");
            string path = Path.Combine(Path.GetTempPath(), "pixel-diagnostics-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                session.Export(path); string original = File.ReadAllText(path);
                bool rejected = false; try { session.Export(path); } catch (IOException) { rejected = true; }
                Assert(rejected && File.ReadAllText(path) == original, "Export overwrote existing file");
            }
            finally { File.Delete(path); }
            Directory.CreateDirectory("artifacts");
            File.WriteAllText("artifacts/diagnostics-synthetic.json", json);
        });
        Test("Empty stopped session has no invented success rate", () =>
        {
            var session = new DiagnosticSession(DiagnosticMode.AutomaticReceive);
            session.Finish(DiagnosticEnd.Stopped);
            var r = session.Snapshot();
            Assert(r.Summary.LocalizationRate == null && r.Summary.FirstAcceptedMs == null && r.RealRdpValidation == "not-verified", "Invented empty result");
        });
        return passed;
    }
}
