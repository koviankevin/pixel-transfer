using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PixelTransfer.Core;

namespace PixelTransfer.Windows;

internal static class WindowsSmoke
{
    internal static async Task Run()
    {
        // Launch real WPF windows; verify the same capture path used by the receiver.
        var main = new MainWindow(); main.Show(); await Task.Delay(200); main.Hide();
        var diagnostics = new DiagnosticSession(DiagnosticMode.WindowsSmoke);
        var metrics = NativeScreen.PrimaryMetrics();
        if (metrics.Width <= 0 || metrics.Height <= 0 || metrics.DpiX is not > 0 || metrics.DpiY is not > 0)
            throw new InvalidOperationException("Primary display metrics unavailable on Windows runner.");
        var size = new FrameSize(640, 360);
        var source = FrameCodec.Probe(size);
        string path = Path.Combine(Path.GetTempPath(), "pixel-smoke-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            BitmapAdapter.Save(source, path);
            if (!BitmapAdapter.Load(path).Pixels.AsSpan().SequenceEqual(source.Pixels)) throw new InvalidOperationException("WPF PNG round trip changed pixels.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        var player = new PlayerWindow(_ => source, 1, size, 5, true);
        try
        {
            player.Show(); player.Activate();
            ProbeResult? result = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                await Task.Delay(500);
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                var capture = NativeScreen.Capture(player.ImageArea);
                result = FrameCodec.Measure(capture);
                diagnostics.RecordProbe(result, metrics, capture.Width, capture.Height,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (result?.Passed == true) break;
            }
            if (result?.Passed != true) throw new InvalidOperationException($"Native WPF playback/capture changed pixels or could not be located: {result}");
            player.Close();
            var packet = TransferPacket.Split(new byte[1000], size)[0];
            source = FrameCodec.Render(packet);
            player = new PlayerWindow(_ => source, 1, size, 5);
            player.Show(); player.Activate();
            var tracker = new FrameTracker
            {
                InspectionObserved = (inspection, bounds, ms, full) => diagnostics.RecordInspection(inspection,
                    NativeScreen.PrimaryMetrics(), bounds.Width, bounds.Height, ms,
                    full ? CaptureKind.FullSearch : CaptureKind.TrackedRegion)
            };
            var desktop = NativeScreen.PrimaryArea();
            bool acceptedOnce = false;
            async Task VerifyAuto()
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    await Task.Delay(500);
                    var decoded = tracker.Read(b => NativeScreen.Capture(new(b.X, b.Y, b.Width, b.Height)),
                        new(desktop.X, desktop.Y, desktop.Width, desktop.Height));
                    acceptedOnce |= decoded != null;
                    diagnostics.RecordReceiveCycle(decoded != null, acceptedOnce ? 1 : 0, 1,
                        tracker.TrackingLosses, tracker.Reacquisitions, tracker.FullSearches);
                    var area = player.ImageArea;
                    if (decoded?.Payload.AsSpan().SequenceEqual(packet.Payload) == true &&
                        tracker.Bounds == new FrameBounds(area.X, area.Y, area.Width, area.Height)) return;
                }
                throw new InvalidOperationException("Automatic screen detection/tracking failed.");
            }
            await VerifyAuto();
            await VerifyAuto(); // Uses the tracked rectangle.
            player.Left += player.Left > 80 ? -40 : 40;
            await VerifyAuto(); // Reacquires the moved window.
            if (tracker.Reacquisitions < 1 || tracker.TrackingLosses < 1)
                throw new InvalidOperationException("Moved-window diagnostics missed loss/reacquisition.");
            player.Close();
            byte[] payload = new byte[size.PayloadBytes]; new Random(152).NextBytes(payload);
            packet = TransferPacket.Split(payload, size)[0];
            var zoomImage = new Image { Source = BitmapAdapter.Source(FrameCodec.Render(packet)), Stretch = Stretch.Fill,
                SnapsToDevicePixels = true, UseLayoutRounding = true };
            RenderOptions.SetBitmapScalingMode(zoomImage, BitmapScalingMode.NearestNeighbor);
            var zoom = new Window { Content = zoomImage, SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                UseLayoutRounding = true, Title = "125% decoding test" };
            zoom.Loaded += (_, _) =>
            {
                var dpi = VisualTreeHelper.GetDpi(zoom);
                zoomImage.Width = 800 / dpi.DpiScaleX; zoomImage.Height = 450 / dpi.DpiScaleY;
            };
            try
            {
                zoom.Show(); zoom.Activate();
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    await Task.Delay(500);
                    var inspection = FrameCodec.Inspect(NativeScreen.Capture(desktop));
                    if (inspection.Packets.Any(p => p.Packet.Payload.AsSpan().SequenceEqual(payload) && Math.Abs(p.Scale - 1.25) < 0.01))
                    {
                        diagnostics.Finish(DiagnosticEnd.Completed);
                        Directory.CreateDirectory("artifacts");
                        diagnostics.Export("artifacts/diagnostics-windows-smoke.json");
                        using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText("artifacts/diagnostics-windows-smoke.json"));
                        if (report.RootElement.GetProperty("RealRdpValidation").GetString() != "not-verified")
                            throw new InvalidOperationException("Local smoke must not claim real RDP validation.");
                        return;
                    }
                }
                throw new InvalidOperationException("125% WPF display could not be restored.");
            }
            finally { zoom.Close(); }
        }
        finally { player.Close(); main.Close(); }
    }
}
