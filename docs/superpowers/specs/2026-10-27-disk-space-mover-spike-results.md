# Disk Space Mover — Kết quả thử nghiệm VHDX (lớp 0)

- Ngày chạy: 2026-10-28, Windows 11 build 28000 (`Microsoft Windows NT 10.0.28000.0`), .NET 10.
- Spec: `2026-10-27-disk-space-mover-design.md`, mục 9 (lớp 0).
- Code thử nghiệm: một chương trình C# console (net10.0-windows, x64, P/Invoke + `System.Management`) đặt **ngoài repo**, đã bỏ đi. Chương trình làm trọn luồng trong **một** tiến trình chạy quyền admin: diskpart tạo/gắn VHDX, đổ dữ liệu, thu nhỏ, khoá/tháo, chép thô, đổi GPT, mở rộng, chkdsk, so mã băm, tháo VHDX.

**Kết luận: ĐẠT**

## 1. Môi trường thử

- VHDX 4096 MB, expandable, gắn vào máy thành **Disk 1** (`Msft Virtual Disk`, `IsBoot=False`, `IsSystem=False`, `MSFT_Disk.Location` = đường dẫn file VHDX, `MSFT_DiskImage.Number` = 1, là ổ mới xuất hiện sau khi gắn).
- Kiểm tra an toàn chạy **trước mọi thao tác khoá / ghi / resize** (12 lần trong lần chạy thành công): ổ phải đúng là VHDX vừa gắn, không phải boot/system, tên có "Virtual", không nằm trong danh sách ổ được bảo vệ (Disk 0: chứa C:, D:, boot/system). Handle `\\.\PhysicalDrive1` được xác nhận lại bằng `IOCTL_STORAGE_GET_DEVICE_NUMBER`; handle volume được xác nhận bằng `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` (1 extent, đúng Disk 1, đúng offset). Mọi lần ghi thô bị giới hạn trong vùng đích của phân vùng đang dời và trong vùng usable của GPT. Chế độ `--probe` (không admin) xác nhận Disk 0 bị từ chối (`IsBoot=True IsSystem=True`).
- Ổ thật (Disk 0) không bị mở để ghi lần nào.

Bố cục sau Step 1 (diskpart tự tạo thêm phân vùng MSR 16 MB khi `convert gpt`):

| # | Phân vùng | Offset (byte) | Kích thước | Ghi chú |
|---|---|---|---|---|
| 1 | MSR | 17 408 | 16 759 808 | `e3c9e316-…`, tự tạo |
| 2 | CLIKE (E:) | 16 777 216 | 1 GiB | NTFS |
| 3 | RLIKE | 1 090 519 040 | 300 MiB | type `de94bba4-…`, attributes `0x8000000000000001`, không có ký tự ổ |
| 4 | DLIKE (F:) | 1 405 091 840 | 2 GiB | NTFS |

## 2. Các bước và số liệu

| Bước | Kết quả |
|---|---|
| Step 2 — đổ dữ liệu | 2000 file trong 50 thư mục, từ 1 024 byte đến 8 388 608 byte, tổng 1 066 085 722 byte (~1,0 GB), 3,5 s; đọc lại kiểm tra 0 sai |
| Step 3 — thu nhỏ DLIKE | `GetSupportedSize`: min 1 335 894 016, max 2 889 858 560; `MSFT_Partition.Resize` ReturnValue = 0; 2 147 483 648 → 1 610 612 736 byte (−512 MiB), 1,2 s |
| Step 4 — dời DLIKE | offset 1 405 091 840 → 1 941 962 752 (+536 870 912), độ dài 1 610 612 736 |
| Step 4.5 — dời RLIKE | offset 1 090 519 040 → 1 627 389 952 (+536 870 912), độ dài 314 572 800 |
| Step 4.6 — mở rộng CLIKE | `GetSupportedSize` max = 1 610 612 736; Resize ReturnValue = 0; 1 073 741 824 → 1 610 612 736 (+536 870 912 đúng 512 MiB) |
| Tổng thời gian chạy | 40,6 s |

## 3. Kết quả từng câu hỏi

### (a) Ghi thô vào vùng của volume đã khoá + tháo qua `\\.\PhysicalDriveN` — **ĐẠT**

- `FSCTL_LOCK_VOLUME` thành công ngay lần đầu (DLIKE: 1 270 ms lần khoá đầu tiên do phải flush 1 GB vừa ghi; lần khoá lại 25 ms; RLIKE: 17 ms). `FSCTL_DISMOUNT_VOLUME` thành công (mã 0).
- `\\.\PhysicalDrive1` mở với `FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH`, bộ đệm căn 4096 byte, khối 16 MiB, từ cao xuống thấp; mỗi khối: đọc nguồn → ghi đích → `FlushFileBuffers` → đọc lại → so sánh.
- DLIKE: 96 khối, **64 khối ghi vào vùng thuộc volume đang khoá** (phần chồng lấn giữa vị trí cũ và mới) + 32 khối vào vùng trống; **0 lỗi ghi, 0 lần so sánh sai, 0 lần thử lại**. 1 610 612 736 byte trong 2,43 s (~632 MB/s).
- RLIKE: 19 khối, toàn bộ vào vùng trống (vì 300 MiB < 512 MiB), 0 lỗi, 314 572 800 byte trong 0,38 s (~784 MB/s).
- Lưu ý: tốc độ đo trên VHDX nằm trên SSD NVMe của máy, không đại diện cho ổ HDD.

### (b) Đổi `StartingOffset` của phân vùng GPT đang có bằng `IOCTL_DISK_SET_DRIVE_LAYOUT_EX` — **ĐẠT**

- `IOCTL_DISK_GET_DRIVE_LAYOUT_EX` → sửa `StartingOffset` (đặt `RewritePartition = TRUE` cho mục đó) → `IOCTL_DISK_SET_DRIVE_LAYOUT_EX` → `IOCTL_DISK_UPDATE_PROPERTIES`: **tất cả trả về thành công (mã lỗi 0)**, cho cả DLIKE và RLIKE, trong lúc vẫn giữ handle khoá volume.
- Đọc lại layout: mục phân vùng nằm ở offset mới, giữ nguyên `PartitionId`, `PartitionType`, `Attributes` (RLIKE vẫn `0x8000000000000001`), độ dài và tên. Số thứ tự phân vùng không đổi.
- Không cần dùng phương án dự phòng (xoá mục rồi tạo lại cùng GUID), nên phương án đó **không được chạy thử**.

### (c) Danh tính và dữ liệu sau khi gắn lại — **ĐẠT**

| Thuộc tính DLIKE | Trước | Sau |
|---|---|---|
| Ký tự ổ | F | F (tự gắn lại, không cần gán; volume trả lời ngay khi đóng handle) |
| GUID phân vùng | `5c46a07b-d17e-47f1-9680-3d685b8245a3` | giống hệt |
| Serial volume (32 bit, `GetVolumeInformation`) | `0x36B35872` | giống hệt |
| Serial NTFS 64 bit (`FSCTL_GET_NTFS_VOLUME_DATA`) | `0xEC36B38E36B35872` | giống hệt |
| Đường dẫn volume GUID | `\\?\Volume{5c46a07b-…}\` | giống hệt |

- SHA-256 của cả 2000 file so với `hashes-before.txt`: **2000 khớp, 0 sai, 0 thiếu** (10,1 s).
- `chkdsk F:` (chỉ đọc): **mã thoát 0**, "found no problems". Thêm: `chkdsk E:` (CLIKE) = 0, `chkdsk \\?\Volume{4e66cdfc-…}` (RLIKE) = 0.
- CLIKE to thêm đúng 536 870 912 byte, offset không đổi.
- GPT cuối cùng: đúng 3 phân vùng dữ liệu (+ MSR do diskpart tạo) ở vị trí mới: CLIKE 16 777 216 (1,5 GiB), RLIKE 1 627 389 952, DLIKE 1 941 962 752 — liền nhau, không chồng lấn.

## 4. Điều bất ngờ / ảnh hưởng tới thiết kế

1. **`FSCTL_GET_VOLUME_BITMAP` không dùng được sau khi khoá volume.** Ngay sau `FSCTL_LOCK_VOLUME` thành công, gọi `FSCTL_GET_VOLUME_BITMAP` trên chính handle khoá trả **lỗi 21 (`ERROR_NOT_READY`)**; `FSCTL_GET_NTFS_VOLUME_DATA` trên handle đó cũng trả 21. Lặp lại trên cả DLIKE (2 lần, lần 2 mở lại handle, flush, chờ, khoá lại) và RLIKE (volume không bị resize) — luôn như vậy. Trước khi khoá thì bitmap đọc bình thường (DLIKE 265 030 cluster đã dùng). Có vẻ trên build 28000 NTFS coi volume như đã tháo ngay khi bị khoá. Lần chạy đầu tiên dừng ở đúng chỗ này (VHDX được tháo an toàn); lần chạy thứ hai dùng phương án dự phòng là **chép toàn bộ phân vùng** (1,5 GiB thay vì ~1,1 GB dữ liệu), nên câu hỏi (a)–(c) vẫn trả lời được.
   - **Ảnh hưởng tới spec mục 5.1:** không thể lấy danh sách cluster "sau khi đã khoá" bằng `FSCTL_GET_VOLUME_BITMAP`. Các hướng: (i) chép toàn bộ phân vùng (đơn giản, chậm hơn khi D: còn nhiều chỗ trống); (ii) sau khi khoá + tháo, tự đọc file `$Bitmap` của NTFS bằng đọc thô (MFT record 6) — vẫn đúng tinh thần "lấy sau khi khoá"; (iii) lấy bitmap trước khi khoá rồi so lại — **không an toàn**, không nên dùng. Cần quyết định trước Task 2.
2. **Trường *hidden sectors* của boot sector NTFS** (offset 0x1C) chứa đúng offset cũ tính theo sector (DLIKE 2 744 320, RLIKE 2 129 920). Spike đã cập nhật nó ở cả boot sector chính và dự phòng (dự phòng nằm ở sector cuối của phân vùng = `NumberSectors × 512`) thành giá trị mới (3 792 896 và 3 178 496) trước khi đổi GPT, đúng như spec bước 4. Spike **không** thử trường hợp bỏ qua bước này, nên chưa biết NTFS có cần nó hay không — cứ giữ bước này.
3. Ký tự ổ tự quay lại mà không cần gán; đường dẫn volume GUID trùng GUID phân vùng và không đổi. Spike **không** gỡ ký tự ổ / đặt thuộc tính ẩn như spec bước 2, nên chưa kiểm chứng phần đó.
4. diskpart `convert gpt` tự tạo phân vùng MSR 16 MB ở đầu đĩa, nên đĩa có 4 mục GPT (3 phân vùng theo kế hoạch + MSR).
5. Kiểm tra phụ "hash thô RLIKE trước/sau" cho hai giá trị khác nhau (`76BAE298…` / `869B6503…`). Nguyên nhân là lỗi thiết kế phép kiểm: hash "sau" được tính **sau** khi đã sửa 2 sector *hidden sectors*. Tính nguyên vẹn của bản chép được chứng minh bằng việc đọc lại và so sánh từng khối (0 sai) và `chkdsk` RLIKE = 0. Đây không phải tiêu chí đạt.

## 5. Giới hạn

- VHDX không phải ổ hệ thống: rủi ro "Windows không cho đổi vị trí phân vùng trên ổ hệ thống đang chạy" (spec mục 11) vẫn còn, chỉ lớp 4 mới trả lời được.
- Không có phần mềm diệt virus / mã hoá bên thứ ba giữ volume trong lần thử (khoá thành công ngay lần đầu).
- Không thử gián đoạn giữa chừng (thuộc lớp 1 và 3).

**Kết luận: ĐẠT** — ghi thô vào volume đã khoá + tháo thành công, `IOCTL_DISK_SET_DRIVE_LAYOUT_EX` đổi được `StartingOffset` (mã 0), volume gắn lại giữ ký tự, GUID, serial; 2000/2000 file khớp; `chkdsk` = 0. Riêng việc lấy bitmap sau khi khoá phải đổi cách làm (mục 4.1) trước Task 2.
