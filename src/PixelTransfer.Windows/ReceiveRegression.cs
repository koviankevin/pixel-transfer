using System.Windows;
using System.Windows.Controls;
using PixelTransfer.Core;

namespace PixelTransfer.Windows;

// Runs the real receive/import continuations on the WPF dispatcher. Only their
// image sources are synthetic, so stopping inside a worker is deterministic.
internal static class ReceiveRegression
{
    internal static async Task Run()
    {
        var size = new FrameSize(640, 360);
        var packets = TransferPacket.Split(new byte[size.PayloadBytes + 1], size);
        foreach (string mode in new[] { "automatic", "manual", "import" })
        foreach (int stopAt in new[] { 1, 2 })
        {
            MainWindow? main = null;
            int reads = 0;
            bool stopDuringRead = true;
            RgbImage Read()
            {
                int index = reads++;
                if (stopDuringRead && reads == stopAt)
                    main!.Dispatcher.Invoke(() => Button(main, "StopButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)));
                return FrameCodec.Render(stopDuringRead && stopAt == 1 ? TransferPacket.Split(new byte[1000], size)[0] : packets[index]);
            }
            main = new MainWindow(_ => Read(), _ => Read());
            try
            {
                var progress = (ProgressBar)main.FindName("ReceiveProgress");
                progress.Value = 75; // Simulate progress left by a previous operation.
                await RunMode(main, mode, stopAt);
                string status = Text(main, "ReceiveStatus").Text;
                if (reads != stopAt || Button(main, "SaveButton").IsEnabled || !status.StartsWith("已停止"))
                    throw new InvalidOperationException($"{mode}: stop during image {stopAt} accepted the in-flight result: {status}");
                if (mode != "import" && !Text(main, "DiagnosticStatus").Text.Contains("Stopped"))
                    throw new InvalidOperationException($"{mode}: cancelled receive was not recorded as stopped.");
                if (!Button(main, "ReceiveButton").IsEnabled || Button(main, "StopButton").IsEnabled)
                    throw new InvalidOperationException($"{mode}: cancellation did not restore controls.");
                if (progress.Value != (stopAt == 1 ? 0 : 50))
                    throw new InvalidOperationException($"{mode}: progress retained old data or included the cancelled frame.");
                // Restart through the same window to prove cancellation state is released.
                reads = 0;
                stopDuringRead = false;
                await RunMode(main, mode, 2);
                if (reads != 2 || !Button(main, "SaveButton").IsEnabled || !Text(main, "ReceiveStatus").Text.StartsWith("已收齊"))
                    throw new InvalidOperationException($"{mode}: restart after stop did not complete.");
            }
            finally { main.Close(); }
        }
    }

    private static Task RunMode(MainWindow main, string mode, int images) => mode == "import"
        ? main.ImportAsync(Enumerable.Range(0, images).Select(i => $"synthetic-{i}.png").ToArray())
        : main.ReceiveAsync(mode == "automatic", new CaptureArea(0, 0, 640, 360));

    private static Button Button(MainWindow main, string name) => (Button)main.FindName(name);
    private static TextBlock Text(MainWindow main, string name) => (TextBlock)main.FindName(name);
}
