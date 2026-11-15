# StashBay

> Giá trung chuyển tệp siêu nhẹ giữa các cửa sổ: kéo vào để tạm giữ, kéo ra để sử dụng — không cần phải mở hai cửa sổ song song trên màn hình.

[简体中文](../../README.md) · [English](../../README.en.md) · [繁體中文](README.zh-Hant.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Español](README.es.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [Português](README.pt.md) · [Русский](README.ru.md) · [Italiano](README.it.md) · [العربية](README.ar.md) · [हिन्दी](README.hi.md) · [বাংলা](README.bn.md) · [ไทย](README.th.md) · [Türkçe](README.tr.md) · **Tiếng Việt** · [Bahasa Indonesia](README.id.md)

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](../../LICENSE) [![365 Open Source Plan #048](https://img.shields.io/badge/365%20Open%20Source%20Plan-%23048-1f6feb)](https://github.com/rockbenben/365opensource)

[⬇ Tải bản mới nhất](https://github.com/rockbenben/048-stashbay/releases/latest)

![Giá đỡ nhỏ gọn StashBay: thẻ nổi luôn trên cùng để lưu trữ và kéo thả tệp](../../assets/shelf.png)

Trạm trung chuyển tệp cục bộ siêu nhẹ dành cho màn hình Windows. Hoạt động hoàn toàn ngoại tuyến, không tải lên đám mây, không yêu cầu mạng và không cần đăng ký tài khoản. Vừa là giá trung chuyển tối giản dùng xong đóng ngay, vừa là bàn làm việc quản lý tệp hoàn chỉnh với khả năng nhận diện Git, sao lưu bản chụp và hoàn tác chỉ bằng một cú nhấp chuột.

---

## Phạm vi hỗ trợ

| Khía cạnh | Thông số hỗ trợ |
| :--- | :--- |
| **Hệ điều hành** | Windows 10 (1809+) / Windows 11 (hỗ trợ gốc cho cả x64 và ARM64) |
| **Môi trường chạy** | Bản cài đặt và bản di động chính thức đều tích hợp sẵn môi trường; bản rút gọn yêu cầu .NET 10 Desktop Runtime |
| **Đối tượng lưu trữ** | Tệp ở mọi định dạng và thư mục nhiều cấp (giữ nguyên hoàn toàn cấu trúc thư mục tương đối) |
| **Phương thức nhập** | Chọn nhiều tệp trong File Explorer, danh sách tệp từ clipboard, cử chỉ lắc chuột |
| **Mục tiêu ghi lại** | Ổ đĩa cục bộ, chia sẻ mạng (UNC / ổ đĩa ánh xạ), kho lưu trữ Git |
| **Tệp dung lượng lớn** | Hỗ trợ các tệp đơn lớn hơn 2GB và đường dẫn dài của Windows (> 260 ký tự) |
| **Phân quyền** | Chạy với quyền người dùng tiêu chuẩn theo mặc định (cài đặt trong `%LOCALAPPDATA%\Programs`); tùy chọn tự khởi động với quyền quản trị |
| **Vị trí dữ liệu** | Lưu mặc định tại `%LOCALAPPDATA%\StashBay\` (tương thích hoàn toàn với thư mục `FileStash` cũ) |

---

## Bắt đầu nhanh

1. **Tải phần mềm**: Tải trình cài đặt `StashBay-vx.y.z-setup.exe` (không cần quyền quản trị) hoặc bản di động `StashBay-vx.y.z-win-x64.zip` từ [Releases](https://github.com/rockbenben/048-stashbay/releases/latest).
2. **Mở giá đỡ**: Nhấn phím tắt <kbd>Win+Alt+S</kbd>; giá đỡ mini sẽ mở ra ngay cạnh con trỏ chuột trên màn hình đang hoạt động.
3. **Thêm tệp**: Chọn tệp trong File Explorer rồi nhấn lại <kbd>Win+Alt+S</kbd> (hoặc kéo tệp vào giá đỡ, hoặc lắc nhẹ chuột) — các tệp sẽ được lưu vào giá đỡ ngay lập tức.
4. **Kéo ra tất cả một lần**: Kéo tay cầm `[⤹ Tổng cộng X mục]` ở cạnh dưới để xuất tất cả tệp vào cửa sổ đích hoặc ứng dụng chat chỉ với một động tác. Khi hết tệp, giá đỡ sẽ tự động mờ dần và đóng lại.

---

## Tính năng cốt lõi

### Giá trung chuyển tối giản (trải nghiệm DropPoint)
* **Kéo ra hàng loạt trong một lần**: Kéo tay cầm đếm số lượng để chuyển toàn bộ tệp sang Explorer, phần mềm chat hoặc biểu mẫu web cùng một lúc.
* **Tự động đóng khi trống**: Các tệp đã chuyển thành công sẽ được gỡ khỏi giá đỡ; khi không còn tệp nào, cửa sổ sẽ nhẹ nhàng ẩn đi.
* **Mở ngay tại vị trí chuột**: Phím tắt toàn cục tự động nhận diện màn hình có con trỏ chuột và mở giá đỡ ngay tại đó.
* **Lắc chuột để lưu tệp**: Khi đang kéo tệp trong Explorer, hãy lắc nhẹ chuột qua lại để mở giá đỡ và lưu tệp tại chỗ.
* **Bóng nổi nam châm bám cạnh**: Nút tròn nhỏ ở mép màn hình, nhấp hoặc kéo nhẹ để mở ra và tự động hít vào cạnh màn hình khi thả tay.
* **Nhiều giá đỡ độc lập**: Nhấp đúp vào biểu tượng khay hệ thống hoặc nhấn <kbd>Ctrl+Shift+N</kbd> để tạo giá tạm thời mới; giá trống sẽ tự động được dọn dẹp.

### Lưu giữ thay đổi nhánh Git và ghi lại an toàn
* **Nhận diện kho lưu trữ**: Ghi nhớ thư mục gốc của các tệp thuộc Git; khi chuyển nhánh, thông báo xuất hiện cho phép khôi phục về vị trí ban đầu chỉ bằng một cú nhấp.
* **Kiểm tra khóa trước khi ghi**: Sử dụng Trình quản lý khởi động lại của Windows để phát hiện các tệp đang bị khóa (bởi Word, Excel hoặc IDE) trước khi ghi đè, tránh hỏng dữ liệu.
* **Bản chụp an toàn trước khi ghi đè**: Tự động sao lưu vào kho lưu trữ Blob cục bộ trước khi ghi đè bất kỳ tệp nào, cho phép hoàn tác tức thì.
* **Bỏ qua tệp trùng khớp qua mã băm**: Bỏ qua việc ghi đè nếu nội dung tệp giống hệt nhau, giữ nguyên thời gian sửa đổi ban đầu và tránh kích hoạt build không cần thiết.

### Bàn làm việc hoàn chỉnh
* **Bốn chế độ xem**: Cấu trúc cây, danh sách phẳng, nhóm theo thư mục nguồn và ô biểu tượng lớn; chế độ xem được lưu nhớ lâu dài.
* **Chế độ khôi phục linh hoạt**: Vị trí ban đầu, thư mục chỉ định hoặc ánh xạ thư mục gốc mới; hỗ trợ đường dẫn tương đối đầy đủ, chỉ tên tệp hoặc làm phẳng thư mục.
* **Xem trước xung đột**: Xem chi tiết các thao tác trước khi thực hiện, với các tùy chọn: Hỏi, Ghi đè, Bỏ qua hoặc Tự động đổi tên.
* **Lịch sử thao tác theo đợt**: Ghi nhật ký chi tiết cho từng đợt áp dụng; nếu có lỗi cục bộ, bạn có thể thử lại riêng cho các tệp thất bại.

### Hỗ trợ 18 ngôn ngữ gốc và giao diện
* Hỗ trợ đầy đủ 18 ngôn ngữ (đảm bảo khớp khóa 100% qua kiểm thử tự động) và hỗ trợ bố cục phản chiếu RTL cho tiếng Ả Rập (ar-SA).
* Tương thích hoàn hảo với giao diện sáng và tối của Windows nhờ tông màu than chì trung tính.

---

## Phím tắt thông dụng

| Phím tắt | Phạm vi | Mô tả |
| :--- | :--- | :--- |
| <kbd>Win+Alt+S</kbd> | Toàn cục | Mở giá đỡ mini; nếu Explorer đang chọn tệp thì đưa tệp vào giá ngay |
| <kbd>Win+Alt+W</kbd> | Toàn cục | Mở bàn làm việc hoàn chỉnh |
| <kbd>Win+Alt+V</kbd> | Toàn cục | Thêm các tệp trong clipboard vào giá đỡ |
| <kbd>Ctrl+V</kbd> | Trong cửa sổ | Dán tệp từ clipboard vào giá đỡ đang hoạt động |
| <kbd>Ctrl+Shift+N</kbd> | Trong cửa sổ | Tạo một giá đỡ trống mới độc lập |
| <kbd>F11</kbd> / <kbd>Ctrl+E</kbd> | Trong cửa sổ | Chuyển đổi qua lại giữa giá đỡ mini và bàn làm việc |
| <kbd>Esc</kbd> | Trong cửa sổ | Trong bàn làm việc thì thu gọn về giá; trong giá thì ẩn xuống khay |
| <kbd>Delete</kbd> | Danh sách | Xóa các mục đã chọn khỏi giá đỡ |

> [!TIP]
> Tất cả các phím tắt toàn cục đều có thể thay đổi hoặc tắt trong Cài đặt. Nếu phím tắt bị xung đột với phần mềm khác, StashBay sẽ hiển thị cảnh báo rõ ràng.

---

## Tham số dòng lệnh (CLI)

`StashBay.exe` cung cấp giao diện dòng lệnh phong phú để tích hợp với các trình khởi chạy hoặc kịch bản tự động:

| Tham số | Mô tả |
| :--- | :--- |
| `--add <đường dẫn...>` | Thêm một hoặc nhiều tệp/thư mục vào giá đỡ |
| `--group <tên nhóm>` | Dùng kèm với `--add` để chỉ định giá đỡ đích (tự tạo nếu chưa có) |
| `--apply-here <thư mục>` | Ghi các tệp trong giá đỡ vào thư mục được chỉ định |
| `--apply-original` | Khôi phục tệp về đường dẫn tương đối ban đầu (rất thích hợp cho Git) |
| `--show` | Hiển thị cửa sổ chính lên trên cùng |
| `--minimized` | Khởi động thu nhỏ vào khay hệ thống (dùng cho tự khởi động cùng Windows) |
| `--quit` | Thoát an toàn phiên bản đang chạy |
| `--unregister-shell` | Gỡ bỏ menu chuột phải, phím tắt Send To và khởi động cùng hệ thống |

---

## Giới hạn đã biết

* **Dành riêng cho Windows**: Tích hợp sâu vào Windows Shell, DWM và hệ thống phân quyền; không có kế hoạch phát triển cho các hệ điều hành khác.
* **Bản chụp thay vì liên kết**: Tệp được tạo bản sao nội dung ngay khi đưa vào; việc chỉnh sửa tệp gốc sau đó không làm thay đổi giá đỡ.
* **Cơ chế một phiên bản**: Khởi chạy lại sẽ tự động chuyển tham số cho tiến trình đang chạy ở khay hệ thống mà không tạo tiến trình thừa.
* **Tự khởi động cùng quyền quản trị**: Mặc định chạy với quyền thông thường. Nếu cần kéo tệp vào các cửa sổ chạy quyền quản trị, hãy bật tùy chọn trong cài đặt và phê duyệt UAC. [Hướng dẫn cấu hình tự khởi động](../admin-startup.md).

---

## Giới thiệu về 365 Open Source Plan

Dự án **#048** của [365 Open Source Plan](https://github.com/rockbenben/365opensource) — một người + AI, hơn 300 dự án mã nguồn mở trong một năm.

[Gửi ý tưởng của bạn →](https://365.aishort.top/) · [Discord](https://discord.gg/PZTQfJ4GjX) · [Telegram](https://t.me/aishort_top)
