# Thành phần mã nguồn mở trong VDM

VDM dùng các phần mềm dưới đây, phân phối theo giấy phép của chính chúng. Cảm ơn các tác giả.
Giấy phép của VDM ([LICENSE](LICENSE)) không hạn chế quyền nào mà các giấy phép này trao cho bạn.

| Thành phần | Dùng để | Giấy phép | Nguồn |
| --- | --- | --- | --- |
| **yt-dlp** | Lấy danh sách chất lượng và tải video | [Unlicense](https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE) (phạm vi công cộng) | [github.com/yt-dlp/yt-dlp](https://github.com/yt-dlp/yt-dlp) |
| **FFmpeg** | Ghép hình với tiếng, chuyển sang MP3 | [GNU GPL v3 trở lên](https://www.gnu.org/licenses/gpl-3.0.html) | xem bên dưới |
| **Deno** | Chạy JavaScript mà yt-dlp cần cho YouTube | [MIT](https://github.com/denoland/deno/blob/main/LICENSE.md) | [github.com/denoland/deno](https://github.com/denoland/deno) |
| **.NET** | Nền chạy của VDM | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) | [github.com/dotnet/runtime](https://github.com/dotnet/runtime) |
| **Microsoft WebView2 SDK** | Hiển thị giao diện (dùng Edge WebView2 có sẵn trong Windows, không kèm theo) | [Giấy phép WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4314.35/License) (kiểu BSD) | [nuget.org/packages/Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) |
| **IBM Plex Sans** | Phông chữ giao diện | [SIL Open Font License 1.1](https://github.com/IBM/plex/blob/master/LICENSE.txt) | [github.com/IBM/plex](https://github.com/IBM/plex) |
| **Inno Setup** | Bộ cài đặt | [Giấy phép Inno Setup](https://jrsoftware.org/files/is/license.txt) | [jrsoftware.org](https://jrsoftware.org/isinfo.php); bản dịch tiếng Việt của memecoder |

## FFmpeg (GNU GPL v3 trở lên)

VDM kèm theo `ffmpeg.exe`, `ffprobe.exe` và các thư viện `av*.dll`, `sw*.dll` trong thư mục `tools` của
VDM, lấy nguyên bản từ bản dựng FFmpeg cho Windows do dự án yt-dlp phát hành (biến thể `win64-gpl-shared`).
VDM chỉ gọi FFmpeg như một chương trình riêng, không sửa đổi nó.

- Phiên bản đi kèm VDM 1.3.0 đến 1.3.2: `N-128170-g0ea6606418-20261028`
- Mã nguồn FFmpeg: [github.com/FFmpeg/FFmpeg](https://github.com/FFmpeg/FFmpeg) (commit `0ea6606418`),
  hoặc [ffmpeg.org/download.html](https://ffmpeg.org/download.html)
- Cách dựng và mã nguồn các thư viện đi kèm (x264, x265, libvpx, dav1d, opus, LAME…):
  [github.com/yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds)

Bạn được thay thế, sửa đổi và phân phối lại FFmpeg theo GNU GPL. Nếu cần bản sao mã nguồn tương ứng
chính xác với bản FFmpeg trong một phiên bản VDM, hãy [mở một Issue](../../issues/new/choose); lời đề nghị
này có hiệu lực ít nhất ba năm kể từ khi phiên bản đó được phát hành.

## Ảnh minh họa

Video trong ảnh chụp màn hình là phim mở *Big Buck Bunny* © Blender Foundation,
[CC BY 3.0](https://creativecommons.org/licenses/by/3.0/), [peach.blender.org](https://peach.blender.org).
