# Pixel Transfer for Windows

透過 RDP 畫面傳送檔案的 C#／WPF 桌面程式。兩台電腦執行同一個 EXE，即可發送、接收及校驗畫面。所有檔案處理在本地完成，程式不會將內容上傳到服務，也不需要 Python。

這是 Windows x64 預覽版。高容量 RGB 傳輸要求 RDP 保持原始像素；請先執行下方的校驗。新 C# 格式不支援 Python 原型的資料圖，兩端都須更新。

本專案採用 [MIT 授權](LICENSE)，允許商用、修改與散布；散布時須保留版權及授權聲明。

## 下載與執行

**v0.1.4-preview 已於 2026-10-23 21:20（台北時間）公開發布，仍為預覽版。** 從[本版 Release](https://github.com/koviankevin/pixel-transfer/releases/tag/v0.1.4-preview) 下載 [PixelTransfer-win-x64.zip](https://github.com/koviankevin/pixel-transfer/releases/download/v0.1.4-preview/PixelTransfer-win-x64.zip)，在兩台 Windows 電腦完整解壓後執行 `PixelTransfer.exe`。發布包自帶 .NET 執行環境，不需另外安裝 Python 或 .NET。

這個儲存庫是公開的，任何人都可以查看原始碼，並直接下載已發布的 Release 附件，不需登入 GitHub。預覽版 EXE 未簽署程式碼簽章。

0.1.4 沿用 [0.1.3 的定位效能改善](https://github.com/koviankevin/pixel-transfer/blob/main/.github/release-notes/v0.1.3-preview.md)，並新增診斷 JSON 匯出、1／10／60 分鐘連續校驗、MIT 授權檔，以及停止接收／匯入後丟棄尚在解析之圖片的修正。新操作會清除舊進度；停止後仍可重新接收。既有 0.1.3 Release 不會隨原始碼更新，也不含這些新增項目。詳見 [0.1.4 版本說明](https://github.com/koviankevin/pixel-transfer/blob/main/.github/release-notes/v0.1.4-preview.md)。

本版來源為 [`57e2d5dd035db32707024159446976003b279e9b`](https://github.com/koviankevin/pixel-transfer/commit/57e2d5dd035db32707024159446976003b279e9b)，已通過合併後 main 的 [Windows CI](https://github.com/koviankevin/pixel-transfer/actions/runs/39959531448) 與[發布流程](https://github.com/koviankevin/pixel-transfer/actions/runs/39960232362)。**真實兩台 Windows 電腦間的 RDP 傳輸仍尚未驗證；CI 成功不代表實際 RDP 已通過。**

下載同版的 [SHA-256 校驗檔](https://github.com/koviankevin/pixel-transfer/releases/download/v0.1.4-preview/PixelTransfer-win-x64.zip.sha256)，在 ZIP 所在目錄以 PowerShell 執行：

```powershell
Get-FileHash .\PixelTransfer-win-x64.zip -Algorithm SHA256
Get-Content .\PixelTransfer-win-x64.zip.sha256
```

兩者的 SHA-256 應一致（不分大小寫）；v0.1.4-preview ZIP 的預期值為 `0eb86199b3d8772705c367d19c02be5c97910118df73de05c0ee8c98eb82272b`。若不一致，請重新下載並核對版本。

若要測試尚未發布的修改，也可以在 [Actions](https://github.com/koviankevin/pixel-transfer/actions) 下載成功建置的 `PixelTransfer-win-x64` 成品；GitHub 要求下載 Actions 成品時登入帳號，成品也有保存期限。請核對該次建置的來源提交與測試結果；使用已發布的 0.1.4 預覽版可直接下載上述 Release ZIP。

### 授權與既有版本

公開分享採用 MIT 授權；下載者可使用、修改、商用及再散布，但須保留版權與授權聲明。v0.1.0-preview 至 v0.1.3-preview 的既有 ZIP 未內附 `LICENSE`；再散布這些版本時，請一併附上[本專案的 MIT 授權聲明](https://github.com/koviankevin/pixel-transfer/blob/4a0a38374670c6c465a5e6264ff362c00c19970b/LICENSE)。原 ZIP、版本標籤及校驗值維持不變，不會因 README 或 main 更新而重新打包。

v0.1.4-preview ZIP 已附上 `LICENSE`，後續 Windows 建置也會附上。發布流程會在建立版本標籤前，確認成品及 ZIP 內的授權聲明與建置來源提交一致；缺少授權的舊成品不能直接作為新 Release 發布。

## 第一次使用：校驗 RDP

1. 在遠端開啟「RDP 校驗」，選擇 **1280 × 720**，按「遠端：顯示校驗圖」。
2. RDP 以 **1:1／100%** 顯示，讓完整圖像與左上、右上、左下定位點都可見。避免游標、通知或其他視窗遮住資料。
3. 本地開啟同一程式的「RDP 校驗」，按「本地：框選並校驗」，拖曳框選整張圖像。
4. **通道不符數為 0**，表示這一次截圖通過逐像素校驗。建議多測幾次，再開始傳輸。

若校驗不通過，先檢查 RDP 是否縮放、是否套用有損編碼或色彩轉換。PNG 只會保留當下截圖，不能修復已經失真的 RDP 畫面。32-bit 色彩或 4:4:4 設定本身也不是無損保證。

「儲存校驗圖」與「檢查已存截圖」可用來離線檢查，輸出必須選擇尚不存在的檔案名稱。

## 可匯出的診斷與重複測試

校驗、自動接收及手動接收會記錄本次數值診斷，完成或停止後在「診斷紀錄」匯出 JSON。RDP 校驗可連續量測 1／10／60 分鐘。報告含本地主螢幕解析度／DPI、定位率、CRC 失敗擷取數、重新定位與時間；不包含畫面、內容、密碼、檔名或傳輸 ID。只保留最近 256 筆明細與完整累計數。

**開始下一次操作前先匯出，關閉程式會遺失未匯出紀錄。** 程式不能自動判定真實 RDP 已通過，診斷一律保留 `RealRdpValidation: not-verified`。請參考 [可重複 RDP 測試步驟及結果範本](docs/RDP-VALIDATION.md)，使用附帶腳本產生合成資料，分開記錄 CI、本地 smoke 與實際 RDP 結果。此功能已包含於 v0.1.4-preview Release；v0.1.3-preview 原有 Release 不會自動更新。

## 發送

1. 在遠端「發送檔案」拖入檔案或按「選擇檔案」。
2. 輸入並確認密碼，選擇資料圖尺寸與換圖間隔。
3. 按「加密並開始播放」。程式先壓縮，再加密、分片及循環播放。
4. 接收完成後，關閉播放視窗或按 Esc 停止。也可按「匯出資料圖」輸出 PNG。

第一輪建議每張停留 **5 秒**，讓 RDP 有時間更新畫面。實際傳輸速度取決於 RDP 和兩端電腦，不保證一輪就收齊。原始檔案上限為 **64 MiB**。

## 接收

1. 將 RDP 移到**主螢幕**，在本地「接收檔案」按「自動尋找並接收」。不需要框選。
2. 讓完整資料圖及三個定位點可見。主程式會最小化以減少遮擋，工作列顯示接收進度。
3. 找到有效資料圖後，只擷取該區域；移動或暫時遮擋後會重新搜尋同一傳輸。讓遠端繼續輪播，重複分片會忽略，漏讀分片在下一輪補齊。
4. 收滿後主視窗會恢復，輸入相同密碼，按「解密並儲存檔案」。

輸出檔案不會覆寫既有檔案；請選擇新的檔案名稱。密碼輸入錯誤可以重試，不必重新接收。接收進度只存在記憶體中；關閉程式或重新開始接收就會清除。

開始搜尋時若有多個不同傳輸，程式會要求只保留一個，或改用「手動框選接收」。手動模式只擷取選定區域，接收期間請保持資料圖位置不動。自動與手動模式目前都使用主螢幕，RDP 校驗仍使用手動框選。

也可按「匯入截圖」一次選取同一傳輸的所有 PNG 截圖。截圖可包含視窗邊框與背景，不能裁掉資料或定位點。

## 視窗尺寸與容量

資料圖按照選定尺寸直接產生並以 1:1 顯示，**不會將已編碼的圖像縮小**。接收端從三點排列辨識圖像尺寸及 0°／90°／180°／270° 方向。90° 旋轉校正只重排像素，不插值。

| RGB 圖像尺寸 | 每張淨承載量 |
| --- | --- |
| 1280 × 720（預設） | 1,852,376 bytes，約 1.85 MB |
| 1600 × 900 | 3,161,816 bytes，約 3.16 MB |
| 1920 × 1080 | 4,816,856 bytes，約 4.82 MB |

「依此電腦可用空間估算」會預留視窗空間，維持 16:9，最高採 1600 × 900。遠端程式無法知道本地 RDP 視窗實際顯示區域；若圖像放不下，請選較小尺寸重新產生，不能直接縮小 RDP 畫面。

0.1.2 起，接收端會辨識不同大小的定位點，依三點間距推算比例並嘗試還原原始像素。自動接收、手動框選與截圖匯入都適用，資料格式相容 0.1.0／0.1.1。嘗試範圍約為 25%～400% 的等比例顯示，優先支援程式產生的 16:9 尺寸；這是搜尋範圍，不代表各種比例都能解碼。RDP 校驗仍檢查 100% 原尺寸像素。

保留像素的放大可能成功，例如測試中的最近鄰 125%、150%、175%、200%、300%、400%。一般看圖程式可能使用平滑插值，即使放大也可能改變 RGB 值；縮小通常會不可逆地合併或丟棄像素。因此仍建議 **100% 顯示**，不能保證 Windows 相片的任意縮放可用。定位點只能協助定位和取樣，不能補回遺失的檔案內容。

接收提示會區分找不到定位點、定位點不完整、已找到三點但資料校驗失敗，以及誤用 RDP 校驗圖。只有分片 CRC 通過才接收，收齊後仍驗證完整資料與 AES-GCM；不會因找到定位點就接受錯誤資料。目前沒有跨畫面糾錯、任意角度旋轉或相機拍攝支援。若每次都被 RDP 改變像素，重播不能解決，必須先處理畫面條件。

## 開發與驗證

- `src/PixelTransfer.Core`：跨平台 C# 核心，包含加密、封包、三點定位與校驗。
- `src/PixelTransfer.Windows`：Windows WPF 介面、DPI 感知播放、GDI 螢幕擷取、PNG 匯入／匯出。
- `tests/PixelTransfer.Tests`：無第三方測試套件依賴的測試執行程式，失敗時返回非零退出碼。
- `legacy/python`：早期 Python 實驗參考，與 C# 新格式不相容，不再作為主要使用入口。

需要 .NET 10 SDK。開發者可執行：

```powershell
dotnet run --project tests/PixelTransfer.Tests -c Release
dotnet run --project src/PixelTransfer.Windows -c Release
```

發布 Windows x64 免安裝成品：

```powershell
dotnet publish src/PixelTransfer.Windows -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/PixelTransfer-win-x64
```

GitHub Actions 執行核心測試、Windows 建置與實際 WPF 視窗／GDI 擷取測試。後者也可在 Windows 以 `PixelTransfer.exe --self-test windows-smoke.txt` 執行。這些測試不等於使用者的真實 RDP 環境測試；不同 DPI、多螢幕、HDR、RDP 編碼設定仍須實機校驗。

`--self-test` 亦涵蓋自動接收、手動接收與截圖匯入：在首張或最後一張圖片處理期間停止，確認不接納該張、不啟用儲存，並驗證同一視窗可重新收齊。單獨執行這六個情境可用 `PixelTransfer.exe --self-test-receive receive-smoke.txt`；測試透過相同的 WPF 接收流程使用合成圖片，不讀取使用者截圖。

維護者發布下一版時，請新增對應的 `.github/release-notes/<新標籤>.md`，並以成功的 main Windows CI run ID、新標籤及版本說明路徑執行 [release.yml](https://github.com/koviankevin/pixel-transfer/blob/main/.github/workflows/release.yml)。流程會附加該次建置的來源提交、CI 及 ZIP SHA-256；不要沿用舊版的固定校驗值，也不要重新發布既有標籤。

## 格式

新格式只在此 C# 版本內使用，不維持 Python 相容性：

- 原始內容以 Brotli 壓縮，只有縮小才採用。
- PBKDF2-HMAC-SHA256（600,000 次，隨機 16-byte salt）產生 AES-256-GCM 金鑰；每次使用隨機 12-byte nonce 與 16-byte 驗證標籤。檔名與長度也納入加密內容。
- `PCE1` 加密封套；`PCF1` 圖像分片帶有 SHA-256 前 16 bytes、分片索引、分片總數、有效長度與圖像尺寸。
- CRC32 篩除傳輸誤讀；整份資料最後以 AES-GCM 驗證。CRC32 不是密碼安全驗證。
- RGB 沒有糾錯冗餘；局部錯誤會重讀整張分片。程式不自動向發送端回傳完成訊號。

應使用足夠長、不易猜測的密碼。所有內容在記憶體處理；大型檔案可能使用數倍於原始大小的記憶體。

## 隱私與監控範圍

自動搜尋會讀取主螢幕像素，包含其他可見視窗；找到資料圖後縮小擷取範圍，失去圖像時重新讀取主螢幕。接收截圖只在記憶體內解析，程式不會自動存檔、上傳或傳送遙測。手動框選可限制讀取區域；使用者主動匯出資料圖或儲存解密檔案時才寫入指定位置。這不保證記憶體內容不會進入 Windows 分頁檔或傾印。

本工具不提供隱藏傳輸或規避監控的保證。公司 EDR、DLP、RDP 工作階段錄影或其他端點管理可能記錄執行程式、存取檔案及畫面。密碼加密保護圖像承載的內容，無法保護已遭監控的來源或接收電腦；來源檔、輸入密碼及解密檔案仍可能被取得。取得資料圖的人也可離線嘗試破解弱密碼。受管理環境應先確認允許這種傳檔方式。
