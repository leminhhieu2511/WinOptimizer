# Disk Space Mover — Thiết kế

- **Ngày:** 2026-10-27
- **Trạng thái:** Chờ duyệt
- **Phạm vi tài liệu:** Thử nghiệm VHDX + Giai đoạn 1 (chuyển ngay trong Windows). Giai đoạn 2 (chương trình chạy lúc khởi động) chỉ được mô tả ở mức kiến trúc; sẽ có spec riêng.

## 1. Bối cảnh và mục tiêu

Người dùng muốn chuyển một phần dung lượng trống từ D: sang C:. Khi thu nhỏ D: trong Disk Management, vùng trống nằm **bên phải D:**, nên không thể "Extend" cho C:, vì vùng trống phải nằm **ngay sát bên phải C:**. Trên nhiều máy, giữa C: và D: còn có **phân vùng Recovery (WinRE)**, nên ngay cả khi xoá D: cũng không mở rộng được C:.

Máy phát triển là ví dụ điển hình:

```
Ổ 0 (UMIS 512 GB, GPT):
[EFI 0.2][MSR][ C: 325.9 GB ][ Recovery 0.85 GB ][ D: 150 GB ]
```

**Mục tiêu:** WinOptimizer làm việc này giống MiniTool Partition Wizard. Nó dịch chuyển thô phân vùng D: (và Recovery) sang phải để chỗ trống nằm ngay sau C:, rồi mở rộng C:.

**Đối tượng:** phát hành cho người dùng bất kỳ. Công cụ phải tự nhận diện bố cục ổ đĩa, **từ chối mọi trường hợp không an toàn**, và giải thích rõ rủi ro.

**Thành công nghĩa là:**
- C: tăng đúng X GB, D: giảm đúng X GB.
- **Toàn bộ dữ liệu trên D: còn nguyên**, D: **giữ nguyên danh tính**: cùng ký tự D:, cùng GUID phân vùng, cùng số serial volume. Nhờ vậy OneDrive, chỉ mục tìm kiếm và phần mềm sao lưu không bị ảnh hưởng.
- Windows khởi động bình thường, WinRE vẫn ở trạng thái như trước khi chuyển.
- Mất điện hoặc crash **ở bất kỳ thời điểm nào** cũng không làm mất dữ liệu. Lần mở WinOptimizer sau sẽ chạy tiếp tới khi hoàn tất.

## 2. Các quyết định đã chốt

| Quyết định | Lựa chọn | Lý do |
|---|---|---|
| Cách chuyển | Dịch chuyển thô phân vùng (giống MiniTool) | Giữ danh tính D:; vẫn chuyển được khi D: gần đầy |
| Ngôn ngữ phần lõi | C/C++ (thư viện tĩnh dùng chung) | Chương trình chạy lúc boot (giai đoạn 2) bắt buộc là native; lõi viết một lần, dùng cho cả hai chế độ |
| Thứ tự làm | Thử nghiệm VHDX → Giai đoạn 1 (trong Windows) → Giai đoạn 2 (boot) | Xác nhận tính khả thi trước khi đầu tư |
| Đọc lại để so sánh | Luôn bật, sau mỗi khối | Phát hiện lỗi ghi ngay; chấp nhận chậm hơn khoảng 50% |
| Ngôn ngữ giao diện | Tiếng Anh, như toàn app | Bản địa hoá cả app để sau |
| Phân vùng Recovery | Dời bằng cùng lõi chép; dự phòng bằng quy trình tạo lại của Microsoft | Giữ nguyên nội dung, ID, thuộc tính |

## 3. Phạm vi

**Giai đoạn 1 hỗ trợ:**
- Mở rộng **phân vùng Windows (C:)** bằng dung lượng X lấy từ **một phân vùng dữ liệu NTFS** nằm sau C: trên **cùng một ổ GPT cơ bản**.
- Giữa C: và phân vùng nguồn chỉ được **không có gì** hoặc có **đúng một phân vùng Recovery**.
- Phân vùng nguồn phải **khoá được** (không chương trình nào đang giữ file).

**Ngoài phạm vi giai đoạn 1** (sẽ bị chặn, kèm giải thích): ổ MBR, Dynamic, Storage Spaces, RAID; nguồn không phải NTFS hoặc bị BitLocker; Windows cài trên nguồn; máy chạy nhiều hệ điều hành; phân vùng khác Recovery nằm giữa; chuyển giữa hai ổ vật lý; dời sang trái; bản ARM64.

**Giai đoạn 2:** `DiskMoveBoot.exe` thực hiện bước 2–5 lúc khởi động, cho trường hợp nguồn không khoá được. Spec riêng.

## 4. Luồng tổng thể

Mỗi bước được ghi vào nhật ký **trước khi làm** và **sau khi xong**.

| # | Bước | Ai làm | Nếu gián đoạn |
|---|---|---|---|
| 0 | Kiểm tra điều kiện (mục 6); `chkdsk` chỉ đọc trên nguồn; lưu bản sao bảng phân vùng (JSON + byte GPT thô) vào `C:\ProgramData\WinOptimizer\DiskMove\` | C# | Chưa thay đổi gì |
| 1 | Thu nhỏ nguồn đi X (`MSFT_Partition.Resize`) | C# | Có thể hủy: trả lại X cho nguồn |
| 2 | Khoá và tháo nguồn (`FSCTL_LOCK_VOLUME`, `FSCTL_DISMOUNT_VOLUME`), gỡ ký tự ổ, đặt thuộc tính GPT ẩn/không gán ký tự | C# | Có thể hủy: bỏ thuộc tính, gắn lại |
| 3 | Dịch dữ liệu nguồn sang phải X (mục 5) | Lõi C/C++ qua DLL | **Không hủy được, chỉ chạy tiếp** |
| 4 | Cập nhật vị trí nguồn trong bảng GPT (`IOCTL_DISK_SET_DRIVE_LAYOUT_EX`); cập nhật trường *hidden sectors* trong boot sector NTFS chính và dự phòng (nếu giá trị vừa 32 bit); bỏ thuộc tính ẩn; gán lại đúng ký tự cũ | Lõi + C# | Chạy tiếp |
| 5 | Ghi lại trạng thái WinRE; `reagentc /disable`; dịch phân vùng Recovery sang phải X bằng cùng lõi và cập nhật GPT; `reagentc /enable` nếu trước đó đang bật. Nếu bật lại thất bại: tạo lại phân vùng Recovery theo quy trình Microsoft (KB5028997) từ `winre.wim` trên C: | C# + lõi | Chạy tiếp |
| 6 | Mở rộng C: thêm X (`MSFT_Partition.Resize`) | C# | Chạy tiếp |
| 7 | Xác minh: `chkdsk` chỉ đọc nguồn; WinRE đúng trạng thái ban đầu; kích thước đúng kế hoạch. Xoá nhật ký, giữ bản sao bảng phân vùng 30 ngày | C# | — |

- Thứ tự bắt buộc: dời phân vùng xa nhất (nguồn) trước, Recovery sau.
- Bước 1, 4, 6 **kiểm tra trạng thái thật của đĩa** trước khi làm, nên chạy lại nhiều lần cũng an toàn.
- Nếu không có phân vùng Recovery thì bỏ qua bước 5.

## 5. Lõi chép và nhật ký

### 5.1 Kế hoạch dịch chuyển (đầu vào của lõi)
- GUID ổ GPT, GUID phân vùng, kích thước sector logic.
- Vị trí bắt đầu cũ (byte), độ dài phân vùng, khoảng dịch **S > 0** (dịch sang phải).
- Danh sách vùng có dữ liệu, tính tương đối với đầu phân vùng:
  - **Phân vùng NTFS:** các cluster đánh dấu "đã dùng" trong `FSCTL_GET_VOLUME_BITMAP`, lấy **sau khi đã khoá** volume, cộng thêm **1 MiB đầu và 1 MiB cuối** phân vùng (boot sector chính và dự phòng).
  - **Phân vùng Recovery:** toàn bộ phân vùng (nhỏ, chép hết cho đơn giản).

### 5.2 Thuật toán
1. Gộp các vùng liền kề, cắt thành khối kích thước **≤ min(16 MiB, S)**, căn theo bội số 4096 byte và sector logic.
2. Xử lý các khối theo **địa chỉ giảm dần**.
3. Mỗi khối: đọc nguồn vào bộ đệm → ghi tới (vị trí + S) → `flush` → đọc lại vùng đích và so sánh với bộ đệm → cập nhật nhật ký → `flush` nhật ký.
4. So sánh không khớp: thử ghi lại tối đa 3 lần. Vẫn sai thì dừng ở trạng thái "lỗi phần cứng". Nguồn của khối đó vẫn nguyên (bất biến 1), nên không mất dữ liệu.

**Bất biến:**
1. Khối ≤ S, nên đích của một khối không bao giờ chồng lên nguồn của chính nó.
2. Xử lý từ cao xuống thấp, nên mỗi lần ghi chỉ đè lên vùng của các khối đã xử lý xong; nguồn của mọi khối chưa xử lý luôn còn nguyên.

**Hệ quả:** chép lại một khối bất kỳ luôn an toàn. Chạy tiếp chỉ cần làm lại từ khối đầu tiên chưa được nhật ký xác nhận.

### 5.3 Nhật ký
- File: `C:\ProgramData\WinOptimizer\DiskMove\journal.bin`. Mở với `FILE_FLAG_WRITE_THROUGH`; mỗi lần ghi đều `flush`. ACL chỉ cho Administrators và SYSTEM.
- **Phần đầu:** chữ ký `WODMJRN1`, phiên bản định dạng, kế hoạch tổng (bước 0–7, X, nguồn, Recovery), kế hoạch dịch chuyển của từng phân vùng, **trạng thái ban đầu cần khôi phục** (ký tự ổ của nguồn, thuộc tính GPT gốc của nguồn, trạng thái WinRE), CRC32C của phần đầu.
- **Hai ô tiến độ A/B**, mỗi ô gồm: số thứ tự tăng dần, bước hiện tại, phân vùng đang dời, số khối đã xong, xxHash64 của khối vừa chép, CRC32C của ô. Mỗi lần ghi chỉ ghi vào ô cũ hơn. Khi đọc, lấy ô hợp lệ có số thứ tự lớn nhất.
- **Kiểm tra khi chạy tiếp:**
  - GUID ổ và GUID phân vùng phải khớp.
  - Bảng phân vùng phải khớp **đúng một** trạng thái dự kiến của bước ghi trong nhật ký (ví dụ: "trước cập nhật GPT" hoặc "sau cập nhật GPT").
  - Không khớp thì dừng, hiện đường dẫn nhật ký và bản sao bảng phân vùng. **Không bao giờ đoán.**

### 5.4 Đọc/ghi
- Ổ vật lý `\\.\PhysicalDriveN` mở với `FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH`; bộ đệm cấp bằng `VirtualAlloc` (căn trang). Giữ handle khoá volume nguồn suốt bước 3–4.
- Lõi không gọi API hệ điều hành. Nó nhận một bảng hàm `read`, `write`, `flush`, `size` cho ổ đĩa và cho nhật ký:
  - `DiskMove.dll`: bản dùng Win32.
  - `DiskMoveBoot.exe` (giai đoạn 2): bản dùng NT native API.
  - Kiểm thử: bản trên file ảnh đĩa, có chèn lỗi.

### 5.5 Giao diện C của lõi
```c
dm_status dm_move  (const dm_io *disk, const dm_io *journal, const dm_plan *plan,
                    dm_progress_cb progress, void *user);
dm_status dm_resume(const dm_io *disk, const dm_io *journal,
                    dm_progress_cb progress, void *user);
dm_status dm_read_journal(const dm_io *journal, dm_journal_info *out);
```
- Tiến độ báo: byte đã chép, tổng byte, tốc độ. Thời gian còn lại do C# tính.
- `progress` trả về giá trị khác 0 để yêu cầu dừng. Lõi chỉ dừng **trước khối đầu tiên** hoặc **giữa hai khối** (dùng cho trường hợp pin yếu). Dừng giữa hai khối luôn để lại trạng thái chạy tiếp được.

`DiskMove.dll` xuất các hàm `dmw_*` cho C# P/Invoke. Mỗi hàm bọc lõi cùng bản đọc/ghi Win32, nhận đường dẫn ổ đĩa, đường dẫn nhật ký và kế hoạch dạng struct.

## 6. Kiểm tra điều kiện

Chạy khi bấm "Analyze" và **chạy lại ngay trước bước 2**.

### 🔴 Chặn
- Không có quyền admin. Có nút "Restart as administrator".
- Nguồn và C: không cùng ổ vật lý.
- Ổ không phải GPT cơ bản: MBR, Dynamic, Storage Spaces, RAID.
- Nguồn không phải NTFS.
- Nguồn có BitLocker (bất kỳ trạng thái nào khác "Fully Decrypted").
- Windows cài trên nguồn, hoặc BCD có nhiều hệ điều hành.
- Giữa C: và nguồn có phân vùng không phải Recovery.
- `chkdsk` chỉ đọc báo lỗi trên nguồn.
- Sức khoẻ ổ (`MSFT_PhysicalDisk.HealthStatus`) khác "Healthy".
- Lượng chuyển tối đa < 1 GiB.

### 🟡 Cần xử lý trước
- **Nguồn không khoá được:** liệt kê chương trình qua Restart Manager (`RmGetList`), có nút "Close for me" cho ứng dụng thường; dịch vụ và driver thì nêu tên và hướng dẫn. Giai đoạn 2 sẽ thêm "Do it on restart".
- **Pagefile nằm trên nguồn:** đề nghị chuyển sang C: rồi khởi động lại.
- **Đang chạy pin:** yêu cầu cắm sạc.
- **Windows đang chờ khởi động lại** để cập nhật (`RebootPending`, `PendingFileRenameOperations`): yêu cầu khởi động lại.
- **Có nhật ký dang dở:** không cho bắt đầu mới, chuyển sang màn hình Resume.

### 🟢 Cảnh báo (phải xác nhận)
- Sao lưu dữ liệu quan trọng: ô bắt buộc tích.
- C: có BitLocker: nhắc giữ sẵn khoá khôi phục và chỉ chỗ tìm khoá.
- OneDrive hoặc phần mềm đồng bộ trên nguồn: sẽ tạm mất ổ vài phút.
- Thời gian ước tính.

### Lượng chuyển
- **Tối đa** = (kích thước nguồn − kích thước nhỏ nhất theo `MSFT_Partition.GetSupportedSize`) − max(1 GiB, 5% dung lượng đã dùng của nguồn), làm tròn xuống theo 1 MiB.
- **Tối thiểu** 1 GiB.

### Trong lúc chạy
- Gọi `SetThreadExecutionState` để chặn máy ngủ, ngủ đông và tắt màn hình.
- Gọi `ShutdownBlockReasonCreate` với lý do *"WinOptimizer is moving disk space — please wait."*
- Rút sạc: hiện cảnh báo lớn. Pin < 20% thì tạm dừng giữa hai khối; cắm sạc lại thì chạy tiếp.

## 7. Giao diện

**Trang mới "Disk Space"** trên thanh bên, đặt giữa *Junk Cleaner* và *Startup Apps*. Trang Home có thêm một ô lối tắt. Toàn bộ chữ trên giao diện bằng tiếng Anh.

Trình hướng dẫn 5 bước:
1. **Disk map:** mỗi ổ vật lý là một thanh, phân vùng vẽ theo tỉ lệ, có tô phần đã dùng. Chọn nguồn từ danh sách; đích cố định là C:. Nút **Analyze**.
2. **Analysis:** danh sách 🔴🟡🟢 kèm nút hành động cho từng dòng 🟡. Có dòng 🔴 thì không đi tiếp được.
3. **Amount:** thanh kéo và ô số (GB) đồng bộ với nhau, có giới hạn tối thiểu/tối đa. Hình xem trước Trước/Sau theo tỉ lệ. Hiện thời gian ước tính và lượng dữ liệu cần chép.
4. **Confirm:** tóm tắt kế hoạch; các cảnh báo; ô "I have backed up important data on D:". Dòng in đậm về ranh giới không thể hủy. Nút **Start moving** chỉ bấm được khi đã tích ô.
5. **Progress → Result:** danh sách 8 bước với trạng thái từng bước; thanh tiến độ, tốc độ, thời gian còn lại ở bước 3 và 5. Nút **Cancel** chỉ có trước bước 3. Màn hình kết quả có kích thước trước/sau, kết quả xác minh, nút "Open Disk Management". Nếu lỗi thì hiện đường dẫn nhật ký và bản sao bảng phân vùng.

**Trong lúc chạy:** chặn đóng cửa sổ (kèm giải thích), khoá thanh bên, không cho "Restart as administrator".

**Màn hình Resume:**
- Khi WinOptimizer khởi động mà thấy nhật ký dang dở, app mở thẳng trang này với banner *"The last disk space move was interrupted at step N (block a of b). Your data is safe — click Continue to finish."*
- Không có lựa chọn bỏ qua.
- Nếu đang ở Limited mode, banner yêu cầu khởi động lại với quyền admin.

**Limited mode:** hiện bản đồ ổ đĩa (chỉ đọc) và nút "Restart as administrator".

**Tiến trình chạy:** trên một luồng nền **ưu tiên bình thường** trong WinOptimizer. Nếu app crash, lần mở sau chạy tiếp từ nhật ký.

## 8. Cấu trúc dự án

```
WinOptimizer/                      (gốc repo)
├─ WinOptimizer.csproj             PlatformTarget x64; target BeforeBuild gọi CMake (configure + build),
│                                  chép DiskMove.dll vào output; loại tests/** và native/** khỏi Compile
├─ Services/DiskSpace/
│  ├─ DiskLayoutService.cs        đọc ổ và phân vùng (MSFT_Disk, MSFT_Partition, MSFT_Volume)
│  ├─ DiskMovePrechecks.cs        các quy tắc 🔴🟡🟢, mỗi quy tắc nhận một bản chụp bố cục
│  ├─ DiskMovePlanner.cs          lượng tối đa/tối thiểu, kế hoạch các bước
│  ├─ DiskMoveRunner.cs           thực thi bước 0–7, gọi DLL, quản lý khoá volume, reagentc
│  ├─ DiskMoveNative.cs           P/Invoke tới DiskMove.dll
│  └─ RestartManagerHelper.cs     liệt kê và đóng chương trình đang giữ file
├─ Pages/DiskSpacePage.xaml(.cs)
├─ native/                         CMake ≥ 3.21, MSVC (VS 2026), C17
│  ├─ DiskMoveCore/               static lib: plan, mover, journal, crc32c, xxhash64
│  ├─ DiskMove/                   DLL: Win32 dm_io + các hàm dmw_*
│  ├─ DiskMoveCli/                CLI chỉ dùng để kiểm thử (không đóng gói khi phát hành)
│  ├─ DiskMoveBoot/               (giai đoạn 2)
│  └─ tests/                      kiểm thử lõi chạy qua CTest
└─ tests/WinOptimizer.Tests/       xUnit
```

CMake dùng bản đi kèm Visual Studio 2026. Lệnh `dotnet build` build được toàn bộ.

## 9. Kiểm thử

**Nguyên tắc tuyệt đối:** trong suốt quá trình phát triển, **không thao tác ghi nào được chạy lên ổ thật của máy phát triển**. Ổ thật chỉ được đọc. Việc chạy thật trên máy thật chỉ được bàn sau khi lớp 4 qua hết, và do chủ dự án quyết định.

| Lớp | Môi trường | Nội dung | Tiêu chí đạt |
|---|---|---|---|
| 0. Thử nghiệm | VHDX gắn vào máy (GPT, phân vùng "C-like" + "Recovery-like" + NTFS dữ liệu) | Khoá/tháo volume; ghi thô vào vùng đã khoá qua `PhysicalDrive`; đổi vị trí bắt đầu phân vùng bằng `IOCTL_DISK_SET_DRIVE_LAYOUT_EX`; gắn lại | Volume gắn lại đúng ký tự, cùng GUID phân vùng và số serial; mã băm mọi file khớp; `chkdsk` sạch. Không đạt thì quay lại thiết kế (cân nhắc làm giai đoạn 2 trước) |
| 1. Lõi | File ảnh đĩa | Dịch với nhiều S và mẫu vùng dữ liệu khác nhau; chèn lỗi ở **mọi lần ghi** (ảnh nhỏ) và 200 vị trí ngẫu nhiên (ảnh lớn), rồi resume; nhật ký bị ghi dở ở ô A/B; ràng buộc kế hoạch | Mọi vùng có dữ liệu giống từng byte sau khi resume; không trường hợp nào mất dữ liệu |
| 2. C# | xUnit, bố cục giả | Lượng tối đa/tối thiểu; mỗi bố cục giả (MBR, BitLocker, OEM chen giữa, không Recovery, hai ổ, nhiều HĐH…) ra đúng mức 🔴🟡🟢 | Tất cả đạt |
| 3. Tích hợp | VHDX bố cục C + Recovery + D, hàng nghìn file | Chạy bước 3–5 thật qua `DiskMoveCli`; tắt tiến trình ngẫu nhiên rồi resume | Mã băm khớp, `chkdsk` sạch, bảng phân vùng đúng kế hoạch |
| 4. Trọn luồng | Máy ảo Hyper-V Windows 11 (C + Recovery + D) | Toàn bộ giao diện; tắt nguồn máy ảo đột ngột ở bước 3 và 5, rồi resume | Windows khởi động bình thường, WinRE đúng trạng thái, D: giữ danh tính, dữ liệu nguyên vẹn |

Lớp 4 cần **Hyper-V được bật** và **ISO Windows 11**; chủ dự án chuẩn bị khi tới bước đó.

## 10. Thứ tự giao việc
1. Lớp 0: thử nghiệm VHDX (code thử nghiệm là đồ bỏ đi, không giữ lại).
2. `DiskMoveCore` + kiểm thử lớp 1.
3. `DiskMove.dll` + `DiskMoveCli` + kiểm thử lớp 3.
4. Phần C#: bố cục, kiểm tra điều kiện, lập kế hoạch + kiểm thử lớp 2.
5. `DiskMoveRunner` + trang Disk Space + màn hình Resume.
6. Kiểm thử lớp 4.
7. Giai đoạn 2: spec riêng cho `DiskMoveBoot.exe`.

## 11. Rủi ro còn lại
- **Windows không cho đổi vị trí phân vùng trên ổ hệ thống khi đang chạy.** Lớp 0 kiểm tra trên VHDX, nhưng VHDX không phải ổ hệ thống. Lớp 4 mới xác nhận dứt điểm. Nếu không được, bước 3–5 phải chuyển sang chế độ boot (giai đoạn 2).
- **Driver bên thứ ba** (phần mềm diệt virus, mã hoá, sao lưu cấp khối) có thể giữ hoặc chặn volume. Khi đó công cụ phát hiện qua việc khoá thất bại và dừng an toàn.
- **Ổ HDD rất chậm** có thể khiến bước 3 kéo dài hàng giờ. Phải báo ước tính trước khi người dùng xác nhận.
