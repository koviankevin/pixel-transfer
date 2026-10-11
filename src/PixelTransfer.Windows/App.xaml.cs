using System.Windows;
using System.IO;

namespace PixelTransfer.Windows;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--self-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string path = e.Args.Length > 1 ? e.Args[1] : "windows-smoke.txt";
            try { await WindowsSmoke.Run(); File.WriteAllText(path, "PASS WPF startup, PNG round trip, physical-pixel playback, GDI screen capture, automatic detection and moved-window reacquisition.\n"); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(path, ex.ToString()); Shutdown(1); }
            return;
        }
        MainWindow = new MainWindow(); MainWindow.Show();
    }
}
