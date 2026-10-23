# Windows 停止接收回歸驗證（2026-10-23）

基準：main `3b2470d`。環境：Windows x64、Microsoft .NET SDK 10.0.402。使用合成資料，未連接另一台電腦的 RDP。

## 重現與原因

先加入可替換圖片來源的 WPF 接收測試入口，未修改停止判斷。執行：

```powershell
dotnet build src/PixelTransfer.Windows -c Release
# 使用相容 .NET 10 執行環境，或使用 self-contained publish 後的 EXE。
./src/PixelTransfer.Windows/bin/Release/net10.0-windows/PixelTransfer.exe --self-test-receive receive-before.txt
```

退出碼 1，報告第一個失敗：

```text
automatic: stop during image 1 accepted the in-flight result: 已收齊並通過傳輸校驗。輸入密碼後，按「解密並儲存檔案」。
```

圖片來源在背景工作已開始後，透過 WPF dispatcher 觸發實際「停止」按鈕。`Task.Run(..., token)` 只保護排程，不能撤銷已開始的同步圖片解析；UI continuation 原本直接接納結果。這個測試不依賴排程碰運氣或真實桌面內容。

## 修正與覆蓋

自動／手動接收及匯入均在 `await` 返回後再次檢查 token，再接受分片。新操作將進度歸零。

| 接收方式 | 首張解析中停止 | 最後一張解析中停止 | 各自停止後重新收齊 |
| --- | --- | --- | --- |
| 自動接收 | 通過 | 通過 | 通過 |
| 手動接收 | 通過 | 通過 | 通過 |
| 匯入截圖 | 通過 | 通過 | 通過 |

斷言：不接納被取消的分片、不啟用儲存、狀態顯示停止、按鈕恢復、進度不沿用舊值；自動及手動診斷狀態為 `Stopped`。同一視窗隨後重新接收兩張並成功組裝。

```powershell
dotnet run --project tests/PixelTransfer.Tests -c Release
./tests/Test-WindowsPackage.ps1
PixelTransfer.exe --self-test windows-smoke.txt
```

結果：63 項核心測試、11 項發布包檢查、六種停止及重新開始情境通過；完整自我測試亦通過 WPF 啟動、PNG 往返、原尺寸播放／GDI 擷取、自動定位、移動後重新定位、125% WPF 顯示還原。Windows 編譯零警告、零錯誤。

## 限制

停止會等待目前的同步解析返回。回歸測試使用合成圖片來源，另由既有 smoke 檢查真實 WPF／GDI 路徑；兩者不構成真實 RDP 傳輸證據。真實 RDP、HDR、多螢幕與長時間工作階段仍依 `docs/RDP-VALIDATION.md` 驗證。此報告不代表 GitHub CI 已完成。
