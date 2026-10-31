# Phát triển VDM

Hướng dẫn cho người muốn tự build, sửa hoặc phát hành VDM. Người dùng thông thường chỉ cần
[README](../README.md).

## Cấu trúc

```
src/VDM.Core      Lõi: tải nhiều kết nối (HttpDownloader), yt-dlp (Media), hàng đợi và lưu trạng thái
                  (DownloadManager), chia tốc độ (Bandwidth), cổng localhost cho extension (LocalApi),
                  cài đặt, tự cập nhật (Updater)
src/VDM.Core/Host Phần điều khiển chung cho mọi hệ điều hành: lệnh của giao diện, trạng thái, hộp thoại
                  bắt link; mỗi hệ điều hành chỉ cung cấp IPlatform (mở file, chọn thư mục, khởi động…)
src/VDM.App       Ứng dụng Windows (WPF): cửa sổ WebView2, khay hệ thống
src/VDM.Mac       Ứng dụng macOS (Photino): cửa sổ WebKit, Dock, LaunchAgent; build bằng build/build-mac.sh
src/VDM.App/ui    Giao diện HTML/CSS/JS; mở index.html, capture.html hoặc capture.html?file trong trình
                  duyệt là xem được với dữ liệu mẫu
extension/src     Extension dùng chung cho Chromium và Firefox (manifest riêng cho Firefox)
tests             Test xUnit với máy chủ HTTP giả lập: chia đoạn, mất mạng, tạm dừng, giới hạn kết nối…
extension/test    Test của extension (node --test)
build             Tải công cụ, build extension, vẽ icon, bộ cài Inno Setup, phát hành
```

## Build trên Windows

Cần: Windows 10/11, .NET SDK 10, Inno Setup 6 (`winget install JRSoftware.InnoSetup`), Node.js (để chạy
test extension), Python 3 và Chrome/Edge (chỉ để vẽ lại biểu tượng).

```powershell
.\build.ps1                 # tải công cụ, build extension, chạy test, đóng gói app, tạo bộ cài
.\build.ps1 -SkipTests      # bỏ qua test
.\build.ps1 -SkipInstaller  # chỉ ra thư mục dist\app
python build\make-icons.py  # vẽ lại biểu tượng .ico và icon extension
```

Kết quả: `dist\VDM-Setup-<phiên bản>.exe`. Phiên bản lấy từ `<Version>` trong `src\VDM.App\VDM.App.csproj`.

Chạy thử một VDM thứ hai bên cạnh VDM đang dùng, với dữ liệu và cổng riêng, không đụng cài đặt thật:

```powershell
$env:VDM_DATA = "C:\temp\vdm-test"; .\src\VDM.App\bin\Debug\net10.0-windows\VDM.exe
```

Test tải YouTube thật (cần mạng, chạy sau khi đã có `deps\tools`):

```powershell
$env:VDM_TOOLS = "$PWD\deps\tools"; dotnet test tests\VDM.Core.Tests
```

## Build trên Mac (chip Apple, M1 trở lên)

Bản Mac dùng chung lõi tải, giao diện và extension với bản Windows; chỉ phần vỏ ứng dụng khác (Photino,
hiển thị giao diện bằng WebKit của macOS). Chưa có bộ cài Mac dựng sẵn, nên người dùng Mac tự build:

1. Cài .NET 10 SDK (https://dotnet.microsoft.com/download) và công cụ dòng lệnh Xcode: `xcode-select --install`.
2. Tải **Source code (zip)** ở [trang phát hành](https://github.com/tdphuong97/VDM/releases/latest), giải nén, mở Terminal trong thư
   mục vừa giải nén rồi chạy `bash build/build-mac.sh`.
3. Kết quả: `dist/VDM-<phiên bản>-mac-arm64.dmg`. Mở file, kéo **VDM** vào **Applications**.

Lần đầu mở, nếu macOS báo "Apple không thể kiểm tra VDM…" (app chưa có chữ ký Developer ID): vào **Cài đặt
hệ thống → Quyền riêng tư & Bảo mật**, bấm **Vẫn mở**, hoặc chạy
`xattr -dr com.apple.quarantine /Applications/VDM.app`.

Extension trên Mac: ở bước "Tải tiện ích đã giải nén", chọn thư mục
`~/Library/Application Support/VDM/extension/chromium` (VDM tự chép ra đó khi mở; trong hộp chọn thư mục,
bấm **⌘⇧G** rồi dán đường dẫn). Bấm nút đóng cửa sổ thì VDM thu xuống **Dock** và vẫn tải tiếp; muốn thoát
hẳn, vào **Cài đặt → Thoát VDM**.

Bản Mac mới được thử trên Windows (Photino chạy được cả ở đó). Cần thử trên Mac thật: mở file và thư mục,
chọn thư mục, thông báo, mở cùng máy, ⌘Q, và tải YouTube với yt-dlp/ffmpeg/Deno bản macOS.

## Phát hành bản mới (cập nhật một nút)

Mỗi VDM từ 1.3.0 đọc `releases/latest/download/latest.json` của repo này mỗi ngày; có bản mới thì báo,
bấm **Cập nhật ngay** là tải bộ cài, kiểm dung lượng và SHA-256, cài im lặng (`/VERYSILENT /UPDATE=1`:
chờ VDM đóng, giữ lựa chọn cũ, mở lại VDM), và extension tự nạp lại bản mới.

1. Tăng `<Version>` (VDM.App, VDM.Mac) và `version` trong hai manifest extension.
2. Thêm mục `## <phiên bản>` vào [CHANGELOG.md](../CHANGELOG.md): mỗi dòng một thay đổi, viết cho người dùng.
3. Đưa mã nguồn lên nhánh `main`, rồi `.\build.ps1 -Publish` (hoặc `.\build.ps1` rồi `.\build\publish.ps1`).
   Cần GitHub CLI đã đăng nhập tài khoản có quyền ghi vào repo (`gh auth login`).

Repo phát hành nằm trong hằng số `Updater.Repo` (`src/VDM.Core/Updater.cs`). Thử luồng cập nhật mà không
phát hành: trỏ một VDM thử vào feed riêng bằng `$env:VDM_UPDATE_FEED = "http://127.0.0.1:47990/latest.json"`
cùng với `VDM_DATA`.

## Firefox

Firefox chỉ giữ extension đã được Mozilla ký. Ký một lần cho mỗi phiên bản (miễn phí):

1. Tạo tài khoản tại addons.mozilla.org, lấy API key ở *Developer Hub → Manage API Keys*.
2. `npx web-ext sign --source-dir extension\dist\firefox --channel unlisted --api-key <key> --api-secret <secret>`
3. Đổi tên file `.xpi` nhận được thành `extension\dist\vdm-firefox.xpi`, rồi chạy lại `.\build.ps1 -SkipTests`.
   Khi đó VDM có nút **Cài vào Firefox**.

## Cách VDM làm việc

- **Tải nhiều kết nối:** file bắt đầu là một đoạn; mỗi kết nối rảnh tách đôi đoạn còn nhiều nhất và tải
  nửa sau, nên kết nối xong sớm vẫn tiếp sức cho kết nối chậm tới cuối. Kết nối được mở dần, mỗi cái
  sau khi máy chủ đã trả lời cái trước; máy chủ giới hạn số kết nối thì VDM dùng đúng số được phép.
- **Chia đều tốc độ:** mỗi file tải có một bộ giới hạn tốc độ chỉnh được khi đang chạy; tốc độ của đường
  truyền được đo lại khi tập file đang tải thay đổi và mỗi 30 giây, rồi chia đều (file bị máy chủ giới hạn
  chỉ giữ phần nó dùng). yt-dlp đi qua một proxy cục bộ để giới hạn được cả tốc độ của nó.
- **Tải tiếp:** trạng thái từng đoạn được lưu mỗi 3 giây vào `%LOCALAPPDATA%\VDM\downloads.json`;
  file đang tải nằm cạnh file đích với đuôi `.vdmpart`. Cookie đăng nhập của một file bị xóa khi file tải xong.
- **Video:** yt-dlp (kèm Deno cho YouTube) lấy danh sách chất lượng và tải; FFmpeg ghép hình với tiếng
  và chuyển MP3. Ưu tiên H.264 + AAC để file mở được trên mọi máy.
- **Bảng tin mạng xã hội:** một trang có nhiều video, nên extension tìm link bài đăng gần video nhất
  (không vượt ra ngoài bài: `article`, `role=article`, `aria-posinset`, hoặc khối chứa video thứ hai);
  TikTok lấy mã video từ id của trình phát (`xgwrapper-…`). Facebook phát video thành từng mẩu
  (`bytestart=`), không bao giờ gửi mẩu đó như một file; tham số `efg` của mẩu cho biết video đang phát,
  dùng khi không tìm được link bài.
- **Extension ↔ app:** extension gọi `http://localhost:47913` (đến 47917). App chỉ nhận yêu cầu có header
  `X-VDM` và không đến từ trang web, nên trang web không gửi lệnh cho VDM được.

Giao diện được thiết kế theo bộ hướng dẫn tasteskill (ghi chú trong `.taste-skill/`). Giấy phép các thành
phần đi kèm: [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).
