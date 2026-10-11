using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PixelTransfer.Core;

namespace PixelTransfer.Windows;

internal sealed class PlayerWindow : Window
{
    private readonly Image image = new() { Stretch = Stretch.None, SnapsToDevicePixels = true, UseLayoutRounding = true };
    private readonly TextBlock status = new() { Padding = new Thickness(12, 6, 12, 6), TextAlignment = TextAlignment.Center };
    private readonly DispatcherTimer timer = new();
    private readonly Func<int, RgbImage> render;
    private readonly int total;
    private int index;
    private RgbImage? current;
    internal CaptureArea ImageArea
    {
        get
        {
            if (current == null) throw new InvalidOperationException("圖像尚未顯示。");
            var point = image.PointToScreen(new Point(0, 0));
            return new((int)Math.Round(point.X), (int)Math.Round(point.Y), current.Width, current.Height);
        }
    }
    public PlayerWindow(Func<int, RgbImage> render, int total, FrameSize size, double seconds, bool probe = false)
    {
        var area = NativeScreen.WorkArea();
        if (size.Width + 32 > area.Width || size.Height + 100 > area.Height)
            throw new InvalidOperationException("資料圖加上視窗邊框放不下，請選擇較小尺寸。");
        this.render = render; this.total = total;
        Title = probe ? "Pixel Transfer · RDP 校驗圖" : "Pixel Transfer · 傳送中";
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight; Background = Brushes.White;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var panel = new StackPanel(); panel.Children.Add(image); panel.Children.Add(status); Content = panel;
        Loaded += (_, _) => { Display(); if (!probe && total > 1) timer.Start(); };
        DpiChanged += (_, _) => { if (current != null) ApplyPixels(); };
        timer.Interval = TimeSpan.FromSeconds(seconds);
        timer.Tick += (_, _) => { index = (index + 1) % total; Display(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => timer.Stop();
        void Display()
        {
            current = render(index); ApplyPixels();
            status.Text = probe ? "校驗圖 · 請在本地端校驗這個畫面 · Esc 關閉" : $"{index + 1} / {total} · 每 {seconds:g} 秒換圖 · 持續輪播 · Esc 停止";
        }
    }
    private void ApplyPixels()
    {
        if (current == null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        image.Width = current.Width / dpi.DpiScaleX; image.Height = current.Height / dpi.DpiScaleY;
        image.Source = BitmapAdapter.Source(current, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY);
    }
}
