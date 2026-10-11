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
                if (result?.Passed == true) return;
            }
            throw new InvalidOperationException($"Native WPF playback/capture changed pixels or could not be located: {result}");
        }
        finally { player.Close(); main.Close(); }
    }
}
