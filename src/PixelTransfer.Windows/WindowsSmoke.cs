using System.IO;
using PixelTransfer.Core;

namespace PixelTransfer.Windows;

internal static class WindowsSmoke
{
    internal static async Task Run()
    {
        // Launch real WPF windows; verify the same capture path used by the receiver.
        var main = new MainWindow(); main.Show(); await Task.Delay(200); main.Hide();
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
                var capture = NativeScreen.Capture(player.ImageArea);
                result = FrameCodec.Measure(capture);
                if (result?.Passed == true) break;
            }
            if (result?.Passed != true) throw new InvalidOperationException($"Native WPF playback/capture changed pixels or could not be located: {result}");
            player.Close();
            var packet = TransferPacket.Split(new byte[1000], size)[0];
            source = FrameCodec.Render(packet);
            player = new PlayerWindow(_ => source, 1, size, 5);
            player.Show(); player.Activate();
            var tracker = new FrameTracker();
            var desktop = NativeScreen.PrimaryArea();
            async Task VerifyAuto()
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    await Task.Delay(500);
                    var decoded = tracker.Read(b => NativeScreen.Capture(new(b.X, b.Y, b.Width, b.Height)),
                        new(desktop.X, desktop.Y, desktop.Width, desktop.Height));
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
        }
        finally { player.Close(); main.Close(); }
    }
}
