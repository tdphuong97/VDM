# An toàn

## Chỉ tải VDM từ đây

Bản chính thức của VDM chỉ có ở [trang phát hành của repo này](../../releases). Mỗi bản phát hành ghi mã
SHA-256 của bộ cài. Muốn kiểm tra file đã tải: mở PowerShell trong thư mục chứa file và chạy

```powershell
Get-FileHash .\VDM-Setup-<phiên bản>.exe -Algorithm SHA256
```

rồi so với mã ghi trên trang phát hành. Khác nhau thì đừng chạy file đó.

Khi cập nhật bằng nút **Cập nhật ngay**, VDM tự kiểm tra cả dung lượng lẫn mã SHA-256 trước khi cài, và bỏ
file nếu không khớp.

## Báo lỗ hổng bảo mật

Nếu bạn tìm thấy lỗ hổng bảo mật trong VDM, xin đừng đăng chi tiết công khai. Hãy dùng
[Report a vulnerability](../../security/advisories/new) (báo riêng qua GitHub), chúng tôi sẽ phản hồi và
sửa sớm nhất có thể.
