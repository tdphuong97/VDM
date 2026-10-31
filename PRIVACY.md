# Quyền riêng tư

**Tóm tắt:** VDM không có máy chủ riêng, không tài khoản, không quảng cáo, không đo đếm hay theo dõi. Mọi dữ
liệu nằm trên máy bạn và do bạn kiểm soát. VDM không thu thập và không xử lý dữ liệu cá nhân của bạn ở bất
kỳ đâu ngoài máy của bạn.

## VDM kết nối tới đâu

| Kết nối | Khi nào | Tắt được không |
| --- | --- | --- |
| Trang web bạn tải file hoặc video | Khi bạn tải | Đây là việc chính của VDM |
| `github.com` (trang phát hành của VDM) | Mỗi ngày một lần, để xem có bản mới không | Có: **Cài đặt → Cập nhật VDM** |
| `github.com` (công cụ tải video yt-dlp) | Mỗi tuần một lần, để cập nhật yt-dlp | Có: **Cài đặt → Công cụ tải video** |
| Extension ↔ VDM trên cùng máy (`localhost`, cổng 47913–47917) | Khi trình duyệt chuyển link sang VDM | Gỡ extension |

Kiểm tra bản mới chỉ là tải một tệp nhỏ từ GitHub, giống mở một trang web; VDM không gửi kèm thông tin gì
về bạn hay máy của bạn. GitHub có thể ghi nhận địa chỉ IP như với mọi lượt truy cập
([chính sách của GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement)).

## VDM lưu gì trên máy bạn

Trong thư mục `%LOCALAPPDATA%\VDM`:

- danh sách tải (địa chỉ link, tên file, nơi lưu, tiến độ) và cài đặt của bạn;
- tên các trình duyệt đã kết nối với VDM;
- nhật ký của công cụ tải video cho từng video (để tìm lỗi khi tải không được);
- bộ cài của bản cập nhật trong lúc cập nhật, xóa đi ở lần mở VDM sau đó.

Không có gì trong số này rời khỏi máy. Muốn xóa sạch: gỡ VDM rồi xóa thư mục `%LOCALAPPDATA%\VDM`.

## Cookie đăng nhập

Một số file và video chỉ tải được khi đã đăng nhập (ví dụ video trong nhóm kín). Khi bạn tải qua VDM,
extension chuyển **cookie của đúng trang đó** sang VDM **trên cùng máy**, để VDM tải với tư cách của bạn,
giống như trình duyệt.

- Cookie chỉ dùng cho chính lượt tải đó và không bao giờ được gửi đi nơi nào khác.
- Với YouTube, VDM không dùng cookie của bạn.
- Cookie được lưu kèm file đang tải để tải tiếp được khi mất mạng, và **bị xóa ngay khi file tải xong**
  (từ bản 1.3.1; ở bản 1.3.0, cookie còn lại đến khi bạn xóa file khỏi danh sách tải).
- Với video, cookie được ghi ra một tệp tạm cho công cụ tải video và bị xóa ngay khi tải xong.
- Cửa sổ ẩn danh (Incognito/InPrivate): trình duyệt mặc định không cho extension chạy ở đó; nếu bạn tự
  bật, VDM vẫn để trình duyệt tự tải file trong cửa sổ ẩn danh.

## Quyền của extension và lý do

| Trình duyệt hỏi quyền | VDM dùng để |
| --- | --- |
| Đọc và thay đổi dữ liệu trên các trang web | Hiện nút **Tải video này** / **Tải bài này** trên video và bài hát đang phát, nhận ra video thuộc bài đăng nào, và gửi link sang VDM trên máy. Extension không đọc nội dung bạn gõ, không đọc tin nhắn hay mật khẩu. |
| Quản lý bản tải xuống | Nhận file thay trình duyệt khi bạn bấm tải (giữ Alt để trình duyệt tự tải) |
| Cookie | Tải được file và video cần đăng nhập, như mục trên |
| Theo dõi yêu cầu mạng (webRequest) | Nhận ra luồng video mà trang đang phát. Chỉ xem địa chỉ và kiểu dữ liệu, không đọc hay sửa nội dung |
| Menu chuột phải | Mục **Tải bằng VDM** |
| Lưu trữ | Nhớ cỡ và vị trí của nút trên trang, và việc bạn tạm dừng bắt link |
| Báo thức (alarms) | Cứ 5 phút xem VDM còn chạy trên máy không |

## Thay đổi

Mọi thay đổi của trang này đều xem được trong lịch sử của repo. Câu hỏi về quyền riêng tư:
[mở một Issue](../../issues/new/choose).
