using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PixelTransfer.Windows;

internal sealed class RegionWindow : Window
{
    private readonly Canvas canvas = new();
    private readonly Rectangle rectangle = new() { Stroke = Brushes.OrangeRed, StrokeThickness = 3 };
    private NativeScreen.Point? start;
    public CaptureArea? Selection { get; private set; }
    public RegionWindow()
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true;
        AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(90, 20, 30, 50));
        ShowInTaskbar = false; Cursor = Cursors.Cross; Content = canvas;
        Left = 0; Top = 0;
        Loaded += (_, _) => {
            var dpi = VisualTreeHelper.GetDpi(this);
            Width = NativeScreen.GetSystemMetrics(0) / dpi.DpiScaleX;
            Height = NativeScreen.GetSystemMetrics(1) / dpi.DpiScaleY;
            Activate();
        };
        var message = new TextBlock { Text = "拖曳框選完整資料圖與三個定位點 · Esc 取消", Foreground = Brushes.White, Background = Brushes.Black, Padding = new Thickness(12), FontSize = 20 };
        canvas.Children.Add(message); Canvas.SetLeft(message, 25); Canvas.SetTop(message, 25);
        canvas.Children.Add(rectangle);
        MouseLeftButtonDown += (_, e) => { NativeScreen.GetCursorPos(out var p); start = p; CaptureMouse(); e.Handled = true; };
        MouseMove += (_, _) => {
            if (start == null) return;
            NativeScreen.GetCursorPos(out var p);
            var a = PointFromScreen(new(start.Value.X, start.Value.Y)); var b = PointFromScreen(new(p.X, p.Y));
            Canvas.SetLeft(rectangle, Math.Min(a.X, b.X)); Canvas.SetTop(rectangle, Math.Min(a.Y, b.Y));
            rectangle.Width = Math.Abs(a.X - b.X); rectangle.Height = Math.Abs(a.Y - b.Y);
        };
        MouseLeftButtonUp += (_, _) => {
            if (start == null) return;
            NativeScreen.GetCursorPos(out var p); ReleaseMouseCapture();
            int width = Math.Abs(p.X - start.Value.X), height = Math.Abs(p.Y - start.Value.Y);
            if (width >= 100 && height >= 100)
            {
                Selection = new(Math.Min(p.X, start.Value.X), Math.Min(p.Y, start.Value.Y), width, height);
                DialogResult = true;
            }
            else start = null;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }
}
