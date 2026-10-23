using System.Windows;
using System.IO;

namespace PixelTransfer.Windows;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] is "--self-test" or "--self-test-receive")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string path = e.Args.Length > 1 ? e.Args[1] : "windows-smoke.txt";
            try
            {
                await ReceiveRegression.Run();
                if (e.Args[0] == "--self-test") await WindowsSmoke.Run();
                File.WriteAllText(path, e.Args[0] == "--self-test"
                    ? "PASS WPF startup, PNG round trip, physical-pixel playback, GDI screen capture, automatic detection, moved-window reacquisition, 125% WPF display restoration and receive/import cancellation with restart.\n"
                    : "PASS automatic/manual receive and screenshot import: stop during first/final decode and restart.\n");
                Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(path, ex.ToString()); Shutdown(1); }
            return;
        }
        MainWindow = new MainWindow(); MainWindow.Show();
    }
}
