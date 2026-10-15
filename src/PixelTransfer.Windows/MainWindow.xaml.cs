using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using Microsoft.Win32;
using PixelTransfer.Core;

namespace PixelTransfer.Windows;

public partial class MainWindow : Window
{
    private PlayerWindow? player;
    private IReadOnlyList<TransferPacket>? sendPackets;
    private TransferCollector collector = new();
    private CancellationTokenSource? receiving;
    private byte[]? receivedEnvelope;
    private bool sending;
    private bool closing;
    private DiagnosticSession? diagnostics;

    public MainWindow()
    {
        InitializeComponent();
        TaskbarItemInfo = new TaskbarItemInfo();
        Closed += (_, _) => { closing = true; receiving?.Cancel(); player?.Close(); };
        UpdateCapacity();
    }
    private static FrameSize SelectedSize(ComboBox box)
    {
        string value = (string)((ComboBoxItem)box.SelectedItem).Tag;
        if (value == "auto")
        {
            var area = NativeScreen.WorkArea();
            int units = Math.Min(100, Math.Min((area.Width - 80) / 16, (area.Height - 140) / 9));
            if (units < 40) throw new InvalidOperationException("螢幕可用空間不足。");
            return new(units * 16, units * 9);
        }
        string[] parts = value.Split(','); return new(int.Parse(parts[0]), int.Parse(parts[1]));
    }
    private void UpdateCapacity()
    {
        if (Capacity == null || SendSize.SelectedItem == null) return;
        try { var size = SelectedSize(SendSize); Capacity.Text = $"{size} · 每張約 {size.PayloadBytes / 1_000_000.0:F2} MB。遠端程式無法得知本地 RDP 視窗的可見範圍。"; }
        catch (Exception ex) { Capacity.Text = ex.Message; }
    }
    private void Size_Changed(object sender, SelectionChangedEventArgs e) => UpdateCapacity();
    private void Error(Exception ex)
    {
        if (closing) return;
        Show(); WindowState = WindowState.Normal; Activate();
        MessageBox.Show(this, ex.Message, "Pixel Transfer", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "選擇要傳送的檔案" };
        if (dialog.ShowDialog(this) == true) FilePath.Text = dialog.FileName;
    }
    private void File_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void File_Drop(object sender, DragEventArgs e)
    {
        if (!sending && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && File.Exists(paths[0])) { FilePath.Text = paths[0]; Tabs.SelectedIndex = 0; }
    }
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = FilePath.Text, password = SendPassword.Password;
            if (!File.Exists(path)) throw new InvalidOperationException("請先選擇檔案。");
            if (password.Length == 0 || password != ConfirmPassword.Password) throw new InvalidOperationException("請輸入密碼，並確認兩次輸入相同。");
            var size = SelectedSize(SendSize);
            double seconds = double.Parse((string)((ComboBoxItem)Interval.SelectedItem).Tag, System.Globalization.CultureInfo.InvariantCulture);
            sending = true; SendButton.IsEnabled = ChooseFileButton.IsEnabled = ExportButton.IsEnabled = false;
            SendStatus.Text = "正在壓縮及加密…";
            player?.Close(); sendPackets = null;
            var result = await Task.Run(() => {
                using var file = File.OpenRead(path);
                if (file.Length > CryptoEnvelope.MaxFileBytes) throw new InvalidOperationException("檔案上限為 64 MiB。");
                byte[] data = new byte[(int)file.Length]; file.ReadExactly(data);
                if (file.ReadByte() != -1) throw new InvalidOperationException("讀取期間檔案大小改變，請重試。");
                byte[] encrypted = CryptoEnvelope.Encrypt(data, Path.GetFileName(path), password);
                return (Packets: TransferPacket.Split(encrypted, size), Original: data.Length, Encrypted: encrypted.Length);
            });
            if (closing) return;
            sendPackets = result.Packets;
            var packets = sendPackets;
            SendStatus.Text = $"{result.Original:N0} bytes → 加密後 {result.Encrypted:N0} bytes，共 {packets.Count} 張。關閉播放視窗即可停止。";
            player = new PlayerWindow(i => FrameCodec.Render(packets[i]), packets.Count, size, seconds);
            player.Show();
        }
        catch (Exception ex) { Error(ex); SendStatus.Text = "未開始播放。"; }
        finally { sending = false; SendButton.IsEnabled = ChooseFileButton.IsEnabled = true; ExportButton.IsEnabled = sendPackets != null; }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (sendPackets == null) return;
        var dialog = new OpenFolderDialog { Title = "選擇資料圖輸出位置" };
        if (dialog.ShowDialog(this) != true) return;
        ExportButton.IsEnabled = false;
        try
        {
            var packets = sendPackets;
            string folder = Path.Combine(dialog.FolderName, "pixel-transfer-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(folder);
            await Task.Run(() => { for (int i = 0; i < packets.Count; i++) BitmapAdapter.Save(FrameCodec.Render(packets[i]), Path.Combine(folder, $"frame-{i + 1:D6}.png")); });
            SendStatus.Text = $"已匯出 {packets.Count} 張資料圖：{folder}";
        }
        catch (Exception ex) { Error(ex); }
        finally { ExportButton.IsEnabled = true; }
    }
    private CaptureArea? SelectArea()
    {
        Hide(); var region = new RegionWindow();
        return region.ShowDialog() == true ? region.Selection : null;
    }
    private void SetReceiving(bool active)
    {
        ReceiveButton.IsEnabled = ManualReceiveButton.IsEnabled = ImportButton.IsEnabled = !active; StopButton.IsEnabled = active;
        CheckScreenButton.IsEnabled = CheckFileButton.IsEnabled = RepeatProbeButton.IsEnabled = !active;
        RepeatStopButton.IsEnabled = active;
        DiagnosticExportButton.IsEnabled = !active && diagnostics != null;
    }
    private void Progress()
    {
        ReceiveProgress.Value = collector.Total == 0 ? 0 : 100.0 * collector.Count / collector.Total;
        TaskbarItemInfo.ProgressState = collector.Total == 0 ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.Normal;
        TaskbarItemInfo.ProgressValue = ReceiveProgress.Value / 100;
        ReceiveStatus.Text = collector.Total == 0 ? "尚未找到有效資料圖。請保留三點、使用 1:1 顯示，並先執行 RDP 校驗。" : $"已收到 {collector.Count} / {collector.Total} 張；重複圖像會忽略。";
    }
    private async void Receive_Click(object sender, RoutedEventArgs e) => await ReceiveAsync(true);
    private async void ManualReceive_Click(object sender, RoutedEventArgs e) => await ReceiveAsync(false);
    private async Task ReceiveAsync(bool automatic)
    {
        if (receiving != null) return;
        DiagnosticSession? session = null;
        try
        {
            CaptureArea? area = automatic ? null : SelectArea();
            if (!automatic && area == null) return;
            session = BeginDiagnostics(automatic ? DiagnosticMode.AutomaticReceive : DiagnosticMode.ManualReceive);
            var tracker = new FrameTracker();
            tracker.InspectionObserved = (inspection, bounds, ms, full) => session.RecordInspection(
                inspection, NativeScreen.PrimaryMetrics(), bounds.Width, bounds.Height, ms,
                full ? CaptureKind.FullSearch : CaptureKind.TrackedRegion);
            collector = new(); receivedEnvelope = null; SaveButton.IsEnabled = false;
            receiving = new CancellationTokenSource(); var token = receiving.Token;
            SetReceiving(true); WindowState = WindowState.Minimized; Show();
            ReceiveStatus.Text = automatic ? "正在主螢幕尋找資料圖…" : "正在讀取框選區域…";
            await Task.Delay(300, token);
            while (!collector.Complete)
            {
                token.ThrowIfCancellationRequested();
                var desktop = NativeScreen.PrimaryArea();
                var result = await Task.Run(() =>
                {
                    if (automatic)
                    {
                        var found = tracker.Read(b => NativeScreen.Capture(new(b.X, b.Y, b.Width, b.Height)),
                            new(desktop.X, desktop.Y, desktop.Width, desktop.Height));
                        return (Packet: found, Inspection: tracker.Inspection, Scale: tracker.Scale);
                    }
                    long started = Stopwatch.GetTimestamp();
                    var inspection = FrameCodec.Inspect(NativeScreen.Capture(area!.Value));
                    session.RecordInspection(inspection, NativeScreen.PrimaryMetrics(), area.Value.Width, area.Value.Height,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, CaptureKind.ManualRegion);
                    var first = inspection.Packets.FirstOrDefault();
                    return (Packet: first?.Packet, Inspection: inspection, Scale: first?.Scale ?? 1);
                }, token);
                var packet = result.Packet;
                if (packet != null) collector.Add(packet);
                session.RecordReceiveCycle(packet != null, collector.Count, collector.Total,
                    tracker.TrackingLosses, tracker.Reacquisitions, tracker.FullSearches);
                Progress();
                if (packet == null)
                    ReceiveStatus.Text = (collector.Count > 0 ? $"已收到 {collector.Count} / {collector.Total} 張。" : "") +
                        (result.Inspection.Status == FrameReadStatus.Success ? "畫面顯示另一個傳輸，正在等待原本的檔案。" : result.Inspection.Message);
                else if (Math.Abs(result.Scale - 1) > 0.005)
                    ReceiveStatus.Text += $" 已由約 {result.Scale:P0} 顯示比例還原並通過校驗。";
                if (!collector.Complete) await Task.Delay(automatic && tracker.Bounds == null ? 750 : 200, token);
            }
            receivedEnvelope = collector.Assemble(); SaveButton.IsEnabled = true;
            session.Finish(DiagnosticEnd.Completed);
            ReceiveStatus.Text = "已收齊並通過傳輸校驗。輸入密碼後，按「解密並儲存檔案」。";
        }
        catch (OperationCanceledException) { session?.Finish(DiagnosticEnd.Stopped); ReceiveStatus.Text = "已停止；重新開始會清除接收進度。"; }
        catch (Exception ex) { session?.Finish(DiagnosticEnd.Error); Error(ex); }
        finally
        {
            receiving?.Dispose(); receiving = null; SetReceiving(false); UpdateDiagnosticStatus();
            if (!closing) { Show(); WindowState = WindowState.Normal; Activate(); }
            TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
        }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => receiving?.Cancel();
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "截圖|*.png;*.bmp;*.jpg;*.jpeg", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        SetReceiving(true); SaveButton.IsEnabled = false; collector = new(); receivedEnvelope = null;
        receiving = new CancellationTokenSource(); var token = receiving.Token;
        try
        {
            string? failedReason = null;
            foreach (string path in dialog.FileNames)
            {
                token.ThrowIfCancellationRequested();
                var inspection = await Task.Run(() => FrameCodec.Inspect(BitmapAdapter.Load(path)), token);
                var packet = inspection.Packets.FirstOrDefault()?.Packet;
                if (packet == null) failedReason = inspection.Message;
                if (packet != null) collector.Add(packet);
                Progress();
            }
            if (collector.Complete) { receivedEnvelope = collector.Assemble(); SaveButton.IsEnabled = true; ReceiveStatus.Text = "已收齊，可以解密並儲存。"; }
            else ReceiveStatus.Text = $"已收到 {collector.Count} / {collector.Total} 張。" + failedReason + " 尚未收齊；請選取含所有分片的截圖重新匯入。";
        }
        catch (OperationCanceledException) { ReceiveStatus.Text = "已停止匯入。"; }
        catch (Exception ex) { Error(ex); }
        finally { receiving?.Dispose(); receiving = null; SetReceiving(false); TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None; }
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (receivedEnvelope == null) return;
        SaveButton.IsEnabled = false;
        try
        {
            byte[] encrypted = receivedEnvelope; string password = ReceivePassword.Password;
            var result = await Task.Run(() => CryptoEnvelope.Decrypt(encrypted, password));
            var dialog = new SaveFileDialog { FileName = result.Name, Title = "儲存還原檔案" };
            if (dialog.ShowDialog(this) != true) return;
            using (var file = new FileStream(dialog.FileName, FileMode.CreateNew, FileAccess.Write)) await file.WriteAsync(result.Data);
            ReceiveStatus.Text = $"已解密並儲存 {result.Data.Length:N0} bytes：{dialog.FileName}";
        }
        catch (Exception ex) { Error(ex); }
        finally { SaveButton.IsEnabled = receivedEnvelope != null; }
    }
    private void ShowProbe_Click(object sender, RoutedEventArgs e)
    {
        try { var size = SelectedSize(ProbeSize); var image = FrameCodec.Probe(size); player?.Close(); player = new PlayerWindow(_ => image, 1, size, 5, true); player.Show(); }
        catch (Exception ex) { Error(ex); }
    }
    private void ExportProbe_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var size = SelectedSize(ProbeSize); var dialog = new SaveFileDialog { FileName = $"probe-{size.Width}x{size.Height}.png", Filter = "PNG|*.png" };
            if (dialog.ShowDialog(this) == true) BitmapAdapter.Save(FrameCodec.Probe(size), dialog.FileName);
        }
        catch (Exception ex) { Error(ex); }
    }
    private void ShowProbeResult(ProbeResult? result)
    {
        ProbeStatus.Text = result == null ? "找不到原尺寸三點圖像。請確認框選完整、沒有縮放，並使用 C# 新版校驗圖。" :
            $"{result.Size} · 通道不符 {result.ChangedChannels:N0} / {result.Size.DataBytes:N0}\n受影響像素 {result.ChangedPixels:N0}；最大通道差值 {result.MaxError}\n" +
            (result.Passed ? "這一次校驗通過。建議多測幾次，再開始傳輸。" : "這一次未通過。請調整 RDP 縮放／畫質設定後再測。");
    }
    private DiagnosticSession BeginDiagnostics(DiagnosticMode mode)
    {
        diagnostics = new DiagnosticSession(mode);
        DiagnosticExportButton.IsEnabled = false;
        DiagnosticStatus.Text = "正在記錄本次操作；完成或停止後可匯出。上次結果已由本次取代。";
        return diagnostics;
    }
    private void UpdateDiagnosticStatus()
    {
        if (diagnostics == null) return;
        var report = diagnostics.Snapshot();
        DiagnosticStatus.Text = $"{report.Mode} · {report.End} · {report.ElapsedMs / 1000:F1} 秒\n" +
            $"定位 {report.Summary.LocatedSamples}/{report.Summary.Samples} 次擷取；CRC 失敗擷取 {report.Summary.CrcFailureSamples}；重新定位 {report.Summary.Reacquisitions} 次。\n" +
            "此紀錄不會自動判定真實 RDP 已驗證。請搭配測試步驟記錄環境。";
    }
    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (diagnostics == null || receiving != null) return;
        var dialog = new SaveFileDialog { FileName = $"pixel-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json", Filter = "JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { diagnostics.Export(dialog.FileName); DiagnosticStatus.Text += "\n診斷紀錄已匯出。"; }
        catch (Exception ex) { Error(ex); }
    }
    private async Task<ProbeResult?> MeasureScreenAsync(CaptureArea area, DiagnosticSession session)
    {
        return await Task.Run(() =>
        {
            long started = Stopwatch.GetTimestamp();
            var result = FrameCodec.Measure(NativeScreen.Capture(area));
            session.RecordProbe(result, NativeScreen.PrimaryMetrics(), area.Width, area.Height,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        });
    }
    private async void CheckScreen_Click(object sender, RoutedEventArgs e) => await CheckScreenAsync(false);
    private async void RepeatProbe_Click(object sender, RoutedEventArgs e) => await CheckScreenAsync(true);
    private async Task CheckScreenAsync(bool repeat)
    {
        if (receiving != null) return;
        DiagnosticSession? session = null;
        try
        {
            int seconds = repeat ? int.Parse((string)((ComboBoxItem)ProbeDuration.SelectedItem).Tag) : 0;
            var area = SelectArea(); if (area == null) return;
            session = BeginDiagnostics(DiagnosticMode.ProbeScreen);
            receiving = new CancellationTokenSource(); var token = receiving.Token;
            SetReceiving(true); WindowState = WindowState.Minimized; Show();
            await Task.Delay(300, token);
            long start = Stopwatch.GetTimestamp();
            do
            {
                token.ThrowIfCancellationRequested();
                ShowProbeResult(await MeasureScreenAsync(area.Value, session));
                token.ThrowIfCancellationRequested();
                if (!repeat || Stopwatch.GetElapsedTime(start).TotalSeconds >= seconds) break;
                await Task.Delay(1000, token);
            } while (true);
            session.Finish(DiagnosticEnd.Completed);
        }
        catch (OperationCanceledException) { session?.Finish(DiagnosticEnd.Stopped); }
        catch (Exception ex) { session?.Finish(DiagnosticEnd.Error); Error(ex); }
        finally
        {
            receiving?.Dispose(); receiving = null; SetReceiving(false); UpdateDiagnosticStatus();
            if (!closing) { Show(); WindowState = WindowState.Normal; Activate(); }
        }
    }
    private async void CheckFile_Click(object sender, RoutedEventArgs e)
    {
        if (receiving != null) return;
        var dialog = new OpenFileDialog { Filter = "截圖|*.png;*.bmp;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(this) != true) return;
        var session = BeginDiagnostics(DiagnosticMode.ProbeFile);
        // Reuse the busy guard while decoding so receive/probe operations cannot replace the session.
        receiving = new CancellationTokenSource(); var token = receiving.Token; SetReceiving(true);
        try
        {
            var result = await Task.Run(() =>
            {
                long start = Stopwatch.GetTimestamp();
                var image = BitmapAdapter.Load(dialog.FileName);
                var measured = FrameCodec.Measure(image);
                session.RecordProbe(measured, new(0, 0, null, null), image.Width, image.Height,
                    Stopwatch.GetElapsedTime(start).TotalMilliseconds, fromFile: true);
                return measured;
            });
            token.ThrowIfCancellationRequested();
            ShowProbeResult(result); session.Finish(DiagnosticEnd.Completed);
        }
        catch (OperationCanceledException) { session.Finish(DiagnosticEnd.Stopped); }
        catch (Exception ex) { session.Finish(DiagnosticEnd.Error); Error(ex); }
        finally { receiving?.Dispose(); receiving = null; SetReceiving(false); UpdateDiagnosticStatus(); }
    }
}
