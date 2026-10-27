# Disk Space Mover — Kế hoạch thực hiện (Thử nghiệm VHDX + Giai đoạn 1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** WinOptimizer chuyển được X GB từ phân vùng dữ liệu (D:) sang C: bằng cách dịch thô D: (và Recovery) sang phải, an toàn khi mất điện, chạy ngay trong Windows.

**Architecture:** Lõi C17 `DiskMoveCore` (kế hoạch khối, chép ngược, nhật ký 2 ô) không gọi API hệ điều hành mà nhận bảng hàm `dm_io`. `DiskMove.dll` bọc lõi bằng Win32 và xuất hàm `dmw_*`. Phía C# (`Services/DiskSpace/`) đọc bố cục ổ, kiểm tra điều kiện, lập kế hoạch, điều phối các bước 0–7 và gọi DLL. Trang `DiskSpacePage` là trình hướng dẫn 5 bước.

**Tech Stack:** C17 + CMake (MSVC, VS 2026), .NET 10 WPF (C#), xUnit, System.Management (WMI `root\Microsoft\Windows\Storage`), diskpart (dựng VHDX trong kiểm thử).

**Spec:** `docs/superpowers/specs/2026-10-27-disk-space-mover-design.md`

## Global Constraints

- **Không bao giờ ghi lên ổ thật của máy phát triển.** Mọi thao tác ghi (thu nhỏ, khoá/tháo, chép thô, sửa GPT, mở rộng, reagentc) chỉ chạy trên VHDX hoặc máy ảo. Mọi kiểm thử có ghi phải từ chối chạy nếu ổ đích là ổ đang chứa Windows của máy chạy kiểm thử (`MSFT_Disk.IsBoot` hoặc `IsSystem` = true).
- Kích thước khối: ≤ min(16 MiB, S), căn theo 4096 byte và theo sector logic (512 hoặc 4096).
- Luôn chép từ địa chỉ cao xuống thấp; luôn đọc lại và so sánh sau mỗi khối; thử ghi lại tối đa 3 lần rồi dừng ở `DM_E_VERIFY`.
- Thư mục dữ liệu: `C:\ProgramData\WinOptimizer\DiskMove\`; nhật ký `journal.bin`; ACL chỉ Administrators + SYSTEM.
- Chữ ký nhật ký `WODMJRN1`, phiên bản định dạng `1`.
- Lượng chuyển: tối đa = (kích thước nguồn − `GetSupportedSize` min) − max(1 GiB, 5% dung lượng đã dùng của nguồn), làm tròn xuống theo 1 MiB; tối thiểu 1 GiB.
- Chữ trên giao diện bằng tiếng Anh. Lý do chặn tắt máy: `"WinOptimizer is moving disk space — please wait."`
- App build ở `x64`. `dotnet build` build cả phần native (CMake, generator Visual Studio 18 2026, `-A x64`).
- Mỗi commit kết thúc bằng dòng `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; tiêu đề commit theo kiểu repo: `[Add] …`, `[Fix] …`, `[Update] …`. Làm trên nhánh `develop`.

## Review Focus

- **Ổ bị đánh số lại sau khi khởi động lại** (ví dụ cắm USB: `PhysicalDrive0` thành `PhysicalDrive1`). Chạy tiếp phải tìm ổ theo **GUID ổ GPT** trong nhật ký, không theo số. → kiểm thử `Resume_FindsDiskByGuidWhenNumberChanged` trong Task 11.
- **Ổ sector 4096 (4Kn)**. Lõi phải chạy đúng với `sector_size = 4096`. → kiểm thử `move_4kn_sectors` trong Task 5.
- **D: phân mảnh nặng** (hàng trăm nghìn vùng dữ liệu). Nhật ký và bộ nhớ phải chịu được 200 000 vùng. → kiểm thử `journal_200k_extents` trong Task 4.
- **Phân vùng không bắt đầu ở bội số 1 MiB** (ổ cài từ thời Windows XP: 63 sector). Kiểm tra kế hoạch chỉ đòi căn theo sector và 4096. → kiểm thử `plan_accepts_4k_aligned_non_mib_start` trong Task 3.
- **Recovery nằm sau D: hoặc có phân vùng nằm sau D:**. Chỉ phân vùng nằm *giữa* C: và nguồn mới phải dời. → kiểm thử `Planner_OnlyMovesPartitionsBetweenTargetAndSource` trong Task 9.

---

## Tổng quan file

```
native/
  CMakeLists.txt                      dự án gốc, bật CTest
  DiskMoveCore/
    CMakeLists.txt
    include/diskmove/dm_core.h        kiểu dữ liệu + toàn bộ API công khai của lõi
    src/dm_hash.c                     crc32c, xxh64
    src/dm_plan.c                     kiểm tra kế hoạch, cắt khối
    src/dm_journal.c                  định dạng và đọc/ghi nhật ký
    src/dm_move.c                     thuật toán chép
  DiskMove/
    CMakeLists.txt
    include/diskmove/dmw_api.h        hàm xuất cho C#
    src/dmw_io_win32.c                dm_io trên file/ổ Win32
    src/dmw_volume.c                  khoá/tháo/bản đồ cluster
    src/dmw_layout.c                  đổi vị trí phân vùng GPT, vá boot sector NTFS
    src/dmw_exports.c
  DiskMoveCli/main.c                  CLI chỉ để kiểm thử
  tests/
    CMakeLists.txt
    dm_test.h                         macro assert tối giản
    mem_io.c / mem_io.h               dm_io trên bộ nhớ, có chèn lỗi
    test_hash.c test_plan.c test_journal.c test_move.c test_main.c
Services/DiskSpace/
  DiskModels.cs                       DiskSnapshot, PartitionSnapshot, SystemFacts, …
  DiskLayoutService.cs                đọc WMI (chỉ đọc)
  SystemFactsProbe.cs                 pin, reboot pending, pagefile, BitLocker, BCD, chkdsk
  RestartManagerHelper.cs
  DiskMovePrechecks.cs                quy tắc 🔴🟡🟢 (thuần logic)
  DiskMovePlanner.cs                  lượng tối đa/tối thiểu, danh sách phân vùng cần dời (thuần logic)
  DiskMoveNative.cs                   P/Invoke tới DiskMove.dll
  IDiskMoveBackend.cs                 giao diện thao tác thật (để giả lập trong kiểm thử)
  WindowsDiskMoveBackend.cs           thực thi thật: WMI resize, thuộc tính phân vùng, reagentc, DLL
  DiskMoveRunner.cs                   điều phối bước 0–7, chạy tiếp
  RestoreState.cs                     JSON lưu trong app blob của nhật ký
Pages/DiskSpacePage.xaml(.cs)
tests/WinOptimizer.Tests/             xUnit (logic) + IntegrationTests/ (VHDX, cần admin)
```

---

### Task 0: Cài bộ công cụ C/C++ (do chủ dự án làm)

Máy phát triển chưa có MSVC, Windows SDK và CMake: `VC\Tools\MSVC` không tồn tại. Task 1 không cần chúng; từ Task 2 trở đi thì bắt buộc.

- [ ] **Step 1: Chủ dự án chạy Visual Studio Installer** (cần quyền admin, tải vài GB):

```bash
"C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe" modify --installPath "C:\Program Files\Microsoft Visual Studio\18\Enterprise" --add Microsoft.VisualStudio.Workload.NativeDesktop --add Microsoft.VisualStudio.Component.VC.CMake.Project --includeRecommended --passive
```

- [ ] **Step 2: Kiểm tra**

Run: `& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -find "**\cmake.exe"` và `... -find "VC\Tools\MSVC\*\bin\Hostx64\x64\cl.exe"`
Expected: mỗi lệnh in ra đúng một đường dẫn tồn tại.

---

### Task 1: Thử nghiệm VHDX (lớp 0) — mã bỏ đi

Trả lời câu hỏi khả thi của spec mục 9 (lớp 0) bằng một chương trình C# console **ngoài repo** (thư mục scratch). Chỉ **báo cáo kết quả** được commit.

**Files:**
- Create (ngoài repo): `<scratch>/vhdx-spike/vhdx-spike.csproj`, `Program.cs`, `layout.diskpart.txt`
- Create: `docs/superpowers/specs/2026-10-27-disk-space-mover-spike-results.md`

- [ ] **Step 1: Dựng VHDX bằng diskpart** (`diskpart /s layout.diskpart.txt`, chạy với quyền admin): file `<scratch>/spike.vhdx`, 4096 MB, expandable; `convert gpt`; phân vùng 1: 1024 MB NTFS "CLIKE"; phân vùng 2: 300 MB NTFS, `set id=de94bba4-06d1-4d40-a16a-bfd50179d6ac`, `gpt attributes=0x8000000000000001` (giống Recovery); phân vùng 3: 2048 MB NTFS "DLIKE", gán chữ cái trống.
- [ ] **Step 2: Đổ dữ liệu vào DLIKE:** 2000 file ngẫu nhiên từ 1 KB đến 8 MB (khoảng 1 GB) trong 50 thư mục; ghi SHA-256 của mọi file vào `hashes-before.txt`; ghi lại GUID phân vùng, số serial volume, chữ cái ổ.
- [ ] **Step 3: Thu nhỏ DLIKE 512 MB** bằng `MSFT_Partition.Resize` (WMI).
- [ ] **Step 4: Chương trình thử** (P/Invoke trực tiếp, chạy với quyền admin), từ chối chạy nếu ổ VHDX có `IsBoot` hoặc `IsSystem`:
  1. Mở `\\?\Volume{…}` của DLIKE; `FSCTL_LOCK_VOLUME`; `FSCTL_GET_VOLUME_BITMAP`; `FSCTL_DISMOUNT_VOLUME`.
  2. Mở `\\.\PhysicalDriveN` với `FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH`; chép các cluster đã dùng (cộng 1 MiB đầu và cuối) sang phải 512 MiB, từ cao xuống thấp, khối 16 MiB, đọc lại và so sánh. Ghi lại: các lần ghi vào vùng của volume đã khoá có thành công không.
  3. `IOCTL_DISK_GET_DRIVE_LAYOUT_EX` → sửa `StartingOffset` của DLIKE → `IOCTL_DISK_SET_DRIVE_LAYOUT_EX` → `IOCTL_DISK_UPDATE_PROPERTIES`. Ghi lại mã lỗi nếu có.
  4. Đóng handle volume (mở khoá).
  5. Lặp lại 1–4 cho phân vùng giống Recovery (chép toàn bộ), dịch sang phải 512 MiB.
  6. Mở rộng CLIKE thêm 512 MB bằng `MSFT_Partition.Resize`.
- [ ] **Step 5: Kiểm tra tiêu chí đạt của spec:** DLIKE gắn lại với **cùng chữ cái, cùng GUID phân vùng, cùng số serial**; SHA-256 mọi file khớp `hashes-before.txt`; `chkdsk <chữ>:` (chỉ đọc) trả mã 0; CLIKE to thêm đúng 512 MB; GPT có đúng 3 phân vùng ở vị trí mới.
- [ ] **Step 6: Ghi `…-spike-results.md`:** kết quả từng câu hỏi (ghi thô vào volume đã khoá; đổi `StartingOffset` qua `SET_DRIVE_LAYOUT_EX`; danh tính sau khi gắn lại), mã lỗi gặp phải, thời gian chép, và kết luận **ĐẠT / KHÔNG ĐẠT**.
- [ ] **Step 7: Commit** báo cáo (`[Add] Disk Space Mover VHDX spike results`). Tháo VHDX (`detach vdisk`) và xoá file.
- [ ] **Step 8: CỔNG QUYẾT ĐỊNH.** Nếu KHÔNG ĐẠT: dừng kế hoạch, báo chủ dự án, quay lại thiết kế (spec mục 11). Chỉ làm tiếp Task 2 khi ĐẠT.

---

### Task 2: Khung native + build từ dotnet + hàm băm

**Files:**
- Create: `native/CMakeLists.txt`, `native/DiskMoveCore/CMakeLists.txt`, `native/DiskMoveCore/include/diskmove/dm_core.h`, `native/DiskMoveCore/src/dm_hash.c`, `native/tests/CMakeLists.txt`, `native/tests/dm_test.h`, `native/tests/test_main.c`, `native/tests/test_hash.c`
- Modify: `WinOptimizer.csproj`, `.gitignore` (thêm `native/build/`)

**Interfaces:**
- Produces (trong `dm_core.h`):

```c
typedef enum dm_status {
    DM_OK = 0, DM_E_INVALID_ARG = 1, DM_E_INVALID_PLAN = 2, DM_E_IO = 3, DM_E_VERIFY = 4,
    DM_E_JOURNAL_CORRUPT = 5, DM_E_JOURNAL_MISMATCH = 6, DM_E_STOPPED = 7, DM_E_NOMEM = 8,
    DM_E_NOT_FOUND = 9, DM_E_ACCESS = 10, DM_E_LOCKED = 11
} dm_status;
uint32_t dm_crc32c(uint32_t seed, const void *data, size_t len);   /* seed 0 cho lần đầu */
uint64_t dm_xxh64(const void *data, size_t len, uint64_t seed);
```

- [ ] **Step 1: Viết kiểm thử hỏng** `test_hash.c`:
  - `ASSERT_EQ_U32(dm_crc32c(0, "123456789", 9), 0xE3069283u)`
  - `ASSERT_EQ_U64(dm_xxh64("", 0, 0), 0xEF46DB3751D8E999ull)`
  - `ASSERT_EQ_U64(dm_xxh64("abc", 3, 0), 0x44BC2CF5AD770999ull)`
  - crc32c tính theo 2 phần (`dm_crc32c(dm_crc32c(0,a,n1), a+n1, n2)`) bằng tính một lần.
- [ ] **Step 2: Chạy, xác nhận hỏng** — `cmake -S native -B native/build -G "Visual Studio 18 2026" -A x64` rồi `cmake --build native/build --config Release` → Expected: lỗi link `dm_crc32c` chưa có.
- [ ] **Step 3: Cài đặt** `dm_hash.c`: CRC32C dạng bảng (đa thức đảo 0x82F63B78); XXH64 theo đặc tả chuẩn.
- [ ] **Step 4: Chạy** `ctest --test-dir native/build -C Release --output-on-failure` → Expected: `100% tests passed`.
- [ ] **Step 5: Tích hợp vào dotnet build.** Trong `WinOptimizer.csproj`:
  - `<PlatformTarget>x64</PlatformTarget>`, `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`, `<SelfContained>false</SelfContained>`.
  - `<Compile Remove="tests/**;native/**" />` cùng `Page`/`None Remove` tương ứng.
  - Target `BuildNative` (`BeforeTargets="BeforeBuild"`): tìm `cmake.exe` qua vswhere; `Exec` configure (nếu chưa có `native/build/CMakeCache.txt`) và `--build --config $(Configuration)`.
  - Item `None Include="native/build/DiskMove/$(Configuration)/DiskMove.dll"` với `CopyToOutputDirectory=PreserveNewest` (DLL có từ Task 6; điều kiện `Exists`).
  - Kiểm tra: `dotnet build -c Release` → `Build succeeded`; app vẫn chạy.
- [ ] **Step 6: Commit** `[Add] Native DiskMoveCore skeleton with CRC32C/XXH64 and CMake build`

---

### Task 3: Kế hoạch dịch chuyển và cắt khối

**Files:**
- Modify: `native/DiskMoveCore/include/diskmove/dm_core.h`
- Create: `native/DiskMoveCore/src/dm_plan.c`, `native/tests/test_plan.c`

**Interfaces:**
- Produces:

```c
typedef struct dm_extent { uint64_t offset, length; } dm_extent;   /* byte, tính từ đầu phân vùng */
typedef struct dm_move_plan {
    uint8_t  disk_guid[16], partition_guid[16];
    uint32_t sector_size;              /* 512 hoặc 4096 */
    uint64_t old_start, length, shift; /* byte; dịch sang phải shift */
    uint32_t extent_count;
    const dm_extent *extents;          /* vùng đã dùng; có thể chồng nhau, chưa sắp xếp */
} dm_move_plan;
dm_status dm_plan_validate(const dm_move_plan *p);
/* Gộp vùng + 1 MiB đầu/cuối, cắt khối <= min(16 MiB, shift), căn 4096 & sector, sắp giảm dần. */
dm_status dm_chunks_build(const dm_move_plan *p, dm_extent **out, uint32_t *out_count);
void      dm_free(void *p);
```

- [ ] **Step 1: Viết kiểm thử hỏng** `test_plan.c`:
  - `plan_rejects_bad_values`: `shift == 0`, `sector_size == 1024`, `old_start % 4096 != 0`, `length` không chia hết cho sector, vùng vượt `length`, `shift < 1 MiB` → mỗi cái trả `DM_E_INVALID_PLAN`.
  - `plan_accepts_4k_aligned_non_mib_start`: `old_start = 32256` (63×512) bị từ chối vì không căn 4096; `old_start = 4096*63` được chấp nhận.
  - `chunks_merge_and_head_tail`: length 64 MiB, vùng `{10 MiB, 1 MiB}` và `{10.5 MiB, 2 MiB}` → các khối phủ đúng `[0,1 MiB) ∪ [10 MiB,12.5 MiB) ∪ [63 MiB,64 MiB)` (làm tròn ra biên 4096), không chồng nhau, giảm dần.
  - `chunks_respect_shift_limit`: `shift = 4 MiB`, một vùng 40 MiB → mọi khối ≤ 4 MiB.
  - `chunks_max_16mib`: `shift = 1 GiB` → mọi khối ≤ 16 MiB.
- [ ] **Step 2: Chạy ctest** → Expected: FAIL (`dm_plan_validate` chưa có).
- [ ] **Step 3: Cài đặt** `dm_plan.c` (sắp xếp vùng theo offset, gộp, mở rộng ra biên căn chỉnh, cắt từ cuối về đầu).
- [ ] **Step 4: Chạy ctest** → Expected: PASS.
- [ ] **Step 5: Commit** `[Add] DiskMoveCore move plan validation and chunking`

---

### Task 4: Nhật ký

**Files:**
- Modify: `dm_core.h`
- Create: `native/DiskMoveCore/src/dm_journal.c`, `native/tests/mem_io.c`, `native/tests/mem_io.h`, `native/tests/test_journal.c`

**Interfaces:**
- Consumes: `dm_move_plan`, `dm_extent`, `dm_crc32c` (Task 2–3).
- Produces:

```c
typedef struct dm_io {
    void *ctx;
    dm_status (*read)(void *ctx, uint64_t off, void *buf, uint32_t len);
    dm_status (*write)(void *ctx, uint64_t off, const void *buf, uint32_t len);
    dm_status (*flush)(void *ctx);
} dm_io;
typedef struct dm_journal_info {
    uint32_t move_count;
    uint32_t step;              /* bước 0–7 do C# định nghĩa */
    uint32_t move_index;        /* phép dời đang chạy */
    uint64_t chunks_done;       /* của move_index */
    uint64_t chunk_count[2];    /* tổng khối của từng phép dời */
    uint64_t seq;
} dm_journal_info;
#define DM_MAX_MOVES 2
dm_status dm_journal_create(const dm_io *j, const dm_move_plan *moves, uint32_t move_count,
                            const void *app_blob, uint32_t app_len);
dm_status dm_journal_read(const dm_io *j, dm_journal_info *info,
                          void *app_blob, uint32_t app_cap, uint32_t *app_len);
dm_status dm_journal_set_step(const dm_io *j, uint32_t step);
/* Nội bộ cho dm_move.c: đọc kế hoạch + khối đã lưu; ghi tiến độ */
dm_status dm_journal_load_move(const dm_io *j, uint32_t move_index, dm_move_plan *plan_out,
                               dm_extent **chunks, uint32_t *chunk_count);
dm_status dm_journal_progress(const dm_io *j, uint32_t move_index, uint64_t chunks_done, uint64_t last_hash);
```

Định dạng (little-endian):
- **Phần đầu** (căn 4096): magic `WODMJRN1`, `version=1`, `header_size`, `move_count`, `app_len`, rồi app blob, rồi với mỗi phép dời: `dm_move_plan` (không con trỏ) + `chunk_count` + mảng khối đã cắt (khối lưu sẵn để chạy tiếp không cần đọc lại bản đồ cluster), cuối cùng là CRC32C của toàn bộ phần đầu.
- **Hai ô** 4096 byte tại `header_size` và `header_size + 4096`. Mỗi ô gồm: `seq u64`, `step u32`, `move_index u32`, `chunks_done u64`, `last_hash u64`, `crc32c u32`. Ghi vào ô có `seq` nhỏ hơn (hoặc ô không hợp lệ), với `seq = max + 1`; `flush` sau mỗi lần ghi.

- [ ] **Step 1: Viết `mem_io`:** bộ nhớ động có `fail_after_writes` (lần ghi thứ N chỉ ghi nửa đầu bộ đệm rồi trả `DM_E_IO`; sau đó mọi `write` đều trả `DM_E_IO`) và `writes` (đếm số lần ghi).
- [ ] **Step 2: Viết kiểm thử hỏng** `test_journal.c`:
  - `journal_roundtrip`: tạo với 2 phép dời và app blob 300 byte → `dm_journal_read` trả `move_count=2`, `step=0`, `seq=1`, blob giống hệt; `dm_journal_load_move(…,1,…)` trả đúng khối.
  - `journal_slots_alternate`: 5 lần `dm_journal_set_step` → `seq=6`, `step` là giá trị cuối.
  - `journal_torn_slot_write`: `fail_after_writes` đúng lần ghi ô → đọc lại vẫn hợp lệ, trả **giá trị trước đó**.
  - `journal_corrupt_header`: lật 1 byte trong phần đầu → `DM_E_JOURNAL_CORRUPT`.
  - `journal_200k_extents`: 200 000 vùng → tạo + đọc thành công, `chunk_count` đúng.
- [ ] **Step 3: Chạy ctest** → Expected: FAIL.
- [ ] **Step 4: Cài đặt** `dm_journal.c`.
- [ ] **Step 5: Chạy ctest** → Expected: PASS.
- [ ] **Step 6: Commit** `[Add] DiskMoveCore crash-safe journal`

---

### Task 5: Thuật toán chép

**Files:**
- Modify: `dm_core.h`
- Create: `native/DiskMoveCore/src/dm_move.c`, `native/tests/test_move.c`

**Interfaces:**
- Consumes: Task 3–4.
- Produces:

```c
/* Trả khác 0 để yêu cầu dừng; lõi chỉ dừng giữa hai khối. */
typedef int (*dm_progress_cb)(void *user, uint64_t bytes_done, uint64_t bytes_total);
/* Chạy (hoặc chạy tiếp) phép dời move_index theo nhật ký. Hoàn tất trả DM_OK và ô ghi chunks_done == chunk_count. */
dm_status dm_run_move(const dm_io *disk, const dm_io *journal, uint32_t move_index,
                      dm_progress_cb progress, void *user);
```

- Mỗi khối (theo thứ tự đã lưu): đọc `old_start+off` → ghi `old_start+off+shift` → `flush` → đọc lại, so sánh (sai thì ghi lại, tối đa 3 lần, rồi `DM_E_VERIFY`) → `dm_journal_progress`. Bỏ qua các khối `< chunks_done`. Bộ đệm căn 4096.
- Ghi chú: spec mục 5.5 nêu `dm_move` / `dm_resume`; kế hoạch hợp nhất thành `dm_journal_create` + `dm_run_move` (luôn chạy tiếp từ nhật ký), vì cả hai đường đều đi qua cùng một logic.

- [ ] **Step 1: Viết kiểm thử hỏng** `test_move.c`. Dùng helper `make_image(len, shift, extents, seed)` (dữ liệu ngẫu nhiên có hạt giống) và `assert_moved(image, plan, original)` (mọi byte trong vùng đã dùng + 1 MiB đầu/cuối nằm đúng ở `+shift`):
  - `move_basic`: 64 MiB, shift 8 MiB, 20 vùng ngẫu nhiên → `DM_OK`, `assert_moved`.
  - `move_shift_smaller_than_chunk`: shift 1 MiB → `DM_OK`, `assert_moved`.
  - `move_shift_larger_than_partition`: shift 128 MiB với phân vùng 64 MiB → `DM_OK`.
  - `move_4kn_sectors`: `sector_size = 4096` → `DM_OK`.
  - `move_crash_every_write`: ảnh 8 MiB, shift 1 MiB; với **mọi** N từ 1 đến tổng số lần ghi của một lần chạy sạch: chạy với `fail_after_writes=N` (cùng bộ đếm cho cả ổ và nhật ký), rồi `dm_run_move` lại không lỗi → `DM_OK` và `assert_moved`.
  - `move_crash_random_large`: ảnh 256 MiB, 200 vị trí N ngẫu nhiên (hạt giống cố định) → như trên.
  - `move_verify_failure`: `mem_io` hỏng có chủ đích (lần đọc lại luôn khác) → `DM_E_VERIFY`, và vùng nguồn của khối đó **vẫn nguyên**.
  - `move_stop_between_chunks`: callback trả 1 sau khối thứ 3 → `DM_E_STOPPED`; chạy lại → `DM_OK`, `assert_moved`.
- [ ] **Step 2: Chạy ctest** → Expected: FAIL.
- [ ] **Step 3: Cài đặt** `dm_move.c`.
- [ ] **Step 4: Chạy ctest** → Expected: PASS (ghi lại thời gian chạy `move_crash_every_write`; nếu > 60 s thì giảm kích thước ảnh, không bỏ "mọi N").
- [ ] **Step 5: Commit** `[Add] DiskMoveCore backward chunk mover with verify and resume`

---

### Task 6: DiskMove.dll (Win32) + DiskMoveCli

**Files:**
- Create: `native/DiskMove/CMakeLists.txt`, `include/diskmove/dmw_api.h`, `src/dmw_io_win32.c`, `src/dmw_volume.c`, `src/dmw_layout.c`, `src/dmw_exports.c`, `native/DiskMoveCli/CMakeLists.txt`, `native/DiskMoveCli/main.c`

**Interfaces:**
- Consumes: toàn bộ API lõi.
- Produces (`__declspec(dllexport)`, `__cdecl`, chuỗi `wchar_t*`):

```c
typedef struct dmw_volume dmw_volume;  /* handle mờ: volume đã khoá */
dm_status dmw_volume_lock(const wchar_t *volume_path, dmw_volume **out);        /* FSCTL_LOCK_VOLUME; DM_E_LOCKED nếu bận */
dm_status dmw_volume_used_extents(dmw_volume *v, dm_extent **out, uint32_t *count, uint64_t *used_bytes);
dm_status dmw_volume_dismount(dmw_volume *v);
void      dmw_volume_close(dmw_volume *v);                                       /* đóng handle = mở khoá */
dm_status dmw_journal_create(const wchar_t *path, const dm_move_plan *moves, uint32_t count,
                             const void *app_blob, uint32_t app_len);           /* tạo file mới, ACL Admin+SYSTEM */
dm_status dmw_journal_read(const wchar_t *path, dm_journal_info *info,
                           void *app_blob, uint32_t app_cap, uint32_t *app_len);
dm_status dmw_journal_set_step(const wchar_t *path, uint32_t step);
dm_status dmw_run_move(const wchar_t *disk_path, const wchar_t *journal_path, uint32_t move_index,
                       dm_progress_cb progress, void *user);
/* Idempotent: nếu phân vùng đã ở new_start thì trả DM_OK. */
dm_status dmw_set_partition_start(const wchar_t *disk_path, const uint8_t partition_guid[16], uint64_t new_start);
/* Ghi HiddenSectors = new_start/sector_size vào boot sector NTFS chính và dự phòng; bỏ qua nếu > UINT32_MAX. */
dm_status dmw_patch_ntfs_hidden_sectors(const wchar_t *disk_path, uint64_t start, uint64_t length, uint32_t sector_size);
dm_status dmw_free(void *p);
```

- `dmw_io_win32`: mở `\\.\PhysicalDriveN` với `FILE_FLAG_NO_BUFFERING|FILE_FLAG_WRITE_THROUGH`; file nhật ký với `FILE_FLAG_WRITE_THROUGH`; `flush = FlushFileBuffers`.
- `DiskMoveCli` (không đóng gói khi phát hành): `diskmove-cli move --disk <path> --volume <path> --disk-guid <g> --part-guid <g> --start <b> --length <b> --shift <b> --sector <n> --journal <p>`; `diskmove-cli resume --disk <path> --journal <p> --move <i>`; `diskmove-cli commit --disk <path> --part-guid <g> --start <b>`. In tiến độ dạng `progress <done>/<total>`.

- [ ] **Step 1: Viết kiểm thử hỏng** trong `native/tests/test_win32_io.c` (chạy không cần admin): `dmw_journal_create` trên file tạm → `dmw_journal_read` khớp; `dmw_run_move` với `disk_path` là **file ảnh** (I/O Win32 chấp nhận đường dẫn file) → kết quả giống `move_basic`.
- [ ] **Step 2: Chạy ctest** → Expected: FAIL.
- [ ] **Step 3: Cài đặt** DLL và CLI.
- [ ] **Step 4: Chạy ctest** → Expected: PASS. `dumpbin /exports DiskMove.dll` liệt kê đủ các hàm `dmw_*` ở trên.
- [ ] **Step 5: Commit** `[Add] DiskMove.dll Win32 wrapper and test-only CLI`

---

### Task 7: Kiểm thử tích hợp VHDX (lớp 3) cho phần native

**Files:**
- Create: `tests/WinOptimizer.Tests/WinOptimizer.Tests.csproj` (xUnit, `net10.0-windows`, x64, tham chiếu `WinOptimizer.csproj`), `tests/WinOptimizer.Tests/IntegrationTests/VhdxFixture.cs`, `tests/WinOptimizer.Tests/IntegrationTests/NativeMoveVhdxTests.cs`

**Interfaces:**
- Produces: `VhdxFixture` (dựng/tháo VHDX qua `diskpart /s`, trả `DiskNumber`, `DiskGuid`, các phân vùng, `PopulateFiles(letter, count, seed) -> Dictionary<string,string>` (đường dẫn → SHA-256), `AssertHashes(letter, dict)`, `Chkdsk(letter) -> int`). Dùng lại ở Task 11.
- Mọi kiểm thử tích hợp có `[Trait("Category","Integration")]`, tự `Skip` khi không có quyền admin, và **thất bại ngay** nếu `IsBoot || IsSystem` của ổ đích là true.

- [ ] **Step 1: Viết kiểm thử** `NativeMoveVhdxTests`:
  - `Move_DataPartition_PreservesFilesAndIdentity`: bố cục C-like/Recovery-like/D-like (như Task 1), 2000 file; thu nhỏ D-like 512 MB; chạy `diskmove-cli move` rồi `commit` → cùng chữ cái, GUID và serial; mọi SHA-256 khớp; `Chkdsk == 0`.
  - `Move_KilledRandomly_ResumesCleanly`: lặp 5 lần: khởi chạy `diskmove-cli move`, giết tiến trình sau một khoảng ngẫu nhiên trong 0.2–3 s (hạt giống cố định), rồi `diskmove-cli resume` → kết quả như trên.
- [ ] **Step 2: Chạy (quyền admin)** — `Start-Process dotnet -Verb RunAs -ArgumentList 'test tests/WinOptimizer.Tests --filter Category=Integration --logger trx;LogFileName=int.trx'` → Expected: cả hai PASS (đọc file `.trx`).
- [ ] **Step 3: Commit** `[Add] VHDX integration tests for native partition move`

---

### Task 8: Đọc bố cục ổ (chỉ đọc)

**Files:**
- Create: `Services/DiskSpace/DiskModels.cs`, `Services/DiskSpace/DiskLayoutService.cs`
- Modify: `WinOptimizer.csproj` (thêm `System.Management`)
- Test: `tests/WinOptimizer.Tests/DiskLayoutServiceTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record PartitionSnapshot(int Number, Guid Guid, Guid TypeGuid, long Offset, long Size,
    char? DriveLetter, string? VolumePath, string? FileSystem, long? UsedBytes, ulong GptAttributes,
    bool IsWindowsPartition, bool IsRecovery);
public sealed record DiskSnapshot(int Number, Guid Guid, string FriendlyName, string PartitionStyle, // "GPT" | "MBR" | "RAW"
    bool IsDynamic, bool IsBoot, bool IsSystem, string HealthStatus, int LogicalSectorSize, long Size,
    IReadOnlyList<PartitionSnapshot> Partitions);
public interface IDiskLayoutSource { IReadOnlyList<DiskSnapshot> GetDisks(); }
public sealed class DiskLayoutService : IDiskLayoutSource { … }   // WMI root\Microsoft\Windows\Storage
public static class PartitionTypes { public static readonly Guid Recovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac"); public static readonly Guid BasicData = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"); }
```

- `IsWindowsPartition`: chữ cái ổ trùng với `Path.GetPathRoot(Environment.SystemDirectory)`.
- `IsDynamic`: có phân vùng loại LDM (`5808c8aa-7e8f-42e0-85d2-e1e90434cfb3` / `af9b60a0-1431-4f62-bc68-3311714a69ad`) hoặc `MSFT_Disk.BusType` là `Spaces`.

- [ ] **Step 1: Viết kiểm thử** (chạy trên máy thật, **chỉ đọc**): `GetDisks_ReturnsBootDiskWithWindowsPartition` → có đúng một ổ `IsBoot`, trong đó có đúng một phân vùng `IsWindowsPartition`; các phân vùng sắp theo `Offset` tăng dần; tổng kích thước phân vùng ≤ kích thước ổ.
- [ ] **Step 2: Chạy** `dotnet test tests/WinOptimizer.Tests --filter FullyQualifiedName~DiskLayoutServiceTests` → Expected: FAIL.
- [ ] **Step 3: Cài đặt.**
- [ ] **Step 4: Chạy** → Expected: PASS.
- [ ] **Step 5: Commit** `[Add] Read-only disk layout service for Disk Space Mover`

---

### Task 9: Kiểm tra điều kiện + lập kế hoạch (thuần logic, lớp 2)

**Files:**
- Create: `Services/DiskSpace/DiskMovePrechecks.cs`, `Services/DiskSpace/DiskMovePlanner.cs`
- Modify: `Services/DiskSpace/DiskModels.cs`
- Test: `tests/WinOptimizer.Tests/DiskMovePrecheckTests.cs`, `tests/WinOptimizer.Tests/DiskMovePlannerTests.cs`, `tests/WinOptimizer.Tests/Fakes/LayoutBuilder.cs`

**Interfaces:**
- Consumes: `DiskSnapshot`, `PartitionSnapshot` (Task 8).
- Produces:

```csharp
public sealed record SystemFacts(bool IsAdmin, bool OnBattery, bool RebootPending,
    IReadOnlyList<string> PagefilePaths, IReadOnlyDictionary<char, string> BitLockerStatus, // "FullyDecrypted", …
    int BcdOsCount, int? SourceChkdskExitCode, long? SourceMinSize,
    IReadOnlyList<LockHolder> SourceLockHolders, bool JournalExists);
public sealed record LockHolder(string Name, int ProcessId, bool IsService, bool CanClose);
public enum CheckSeverity { Blocker, FixFirst, Warning }
public enum CheckCode { NotAdmin, DifferentDisk, NotBasicGpt, SourceNotNtfs, SourceBitLocker, WindowsOnSource,
    MultipleOs, ForeignPartitionBetween, SourceFsErrors, DiskUnhealthy, TooLittleSpace,
    SourceInUse, PagefileOnSource, OnBattery, RebootPending, JournalExists,
    Backup, TargetBitLocker, SyncAppsOnSource, Duration }
public sealed record CheckResult(CheckSeverity Severity, CheckCode Code, string Message);
public static class DiskMovePrechecks { public static IReadOnlyList<CheckResult> Run(DiskSnapshot disk, PartitionSnapshot target, PartitionSnapshot source, SystemFacts facts); }
public sealed record MovePlan(long Amount, IReadOnlyList<PartitionSnapshot> PartitionsToShift /* xa nhất trước */, bool HasRecovery);
public static class DiskMovePlanner {
    public const long MiB = 1L << 20, GiB = 1L << 30;
    public static long MaxAmount(PartitionSnapshot source, long sourceMinSize);   // công thức trong Global Constraints
    public static long MinAmount => GiB;
    public static MovePlan Plan(DiskSnapshot disk, PartitionSnapshot target, PartitionSnapshot source, long amount);
}
```

- Thông điệp (`Message`) bằng tiếng Anh, nêu cách xử lý.

- [ ] **Step 1: Viết kiểm thử hỏng.** `LayoutBuilder` dựng bố cục mặc định giống máy phát triển (EFI, MSR, C: 325.9 GB Windows, Recovery 0.85 GB, D: 150 GB NTFS, dùng 36.8 GB) và cho phép sửa từng thuộc tính.
  - Mỗi `CheckCode` có ít nhất một kiểm thử: bố cục/sự kiện gây ra nó → `Run` chứa đúng mã với đúng `Severity` (bảng spec mục 6). Ví dụ `Mbr_IsBlocker`, `OemPartitionBetween_IsBlocker`, `PagefileOnSource_IsFixFirst`, `TargetBitLocker_IsWarning`.
  - `DefaultLayout_HasNoBlockersOrFixFirst`.
  - `MaxAmount_UsesFormula`: nguồn 150 GiB, min 40 GiB, đã dùng 36.8 GiB → `(150−40)GiB − max(1 GiB, 5%·36.8 GiB = 1.84 GiB)`, làm tròn xuống theo MiB.
  - `TooLittleSpace_WhenMaxBelow1GiB` → Blocker.
  - `Planner_OnlyMovesPartitionsBetweenTargetAndSource`: (a) mặc định → dời `[D, Recovery]` theo thứ tự đó; (b) Recovery nằm **sau** D → chỉ dời `[D]`, `HasRecovery=false`; (c) có thêm phân vùng sau D → không bị dời.
  - `Planner_RejectsAmountOutsideRange` → `ArgumentOutOfRangeException`.
- [ ] **Step 2: Chạy** → Expected: FAIL.
- [ ] **Step 3: Cài đặt.**
- [ ] **Step 4: Chạy** `dotnet test tests/WinOptimizer.Tests --filter "Category!=Integration"` → Expected: PASS.
- [ ] **Step 5: Commit** `[Add] Disk Space Mover prechecks and planner`

---

### Task 10: Thu thập sự kiện hệ thống + Restart Manager

**Files:**
- Create: `Services/DiskSpace/SystemFactsProbe.cs`, `Services/DiskSpace/RestartManagerHelper.cs`
- Test: `tests/WinOptimizer.Tests/SystemFactsProbeTests.cs`

**Interfaces:**
- Consumes: `SystemFacts`, `LockHolder` (Task 9).
- Produces: `SystemFactsProbe.Collect(DiskSnapshot disk, PartitionSnapshot source) -> SystemFacts`; `RestartManagerHelper.GetHolders(string rootPath) -> IReadOnlyList<LockHolder>`; `RestartManagerHelper.CloseApps(IEnumerable<LockHolder>) -> bool`.
- Nguồn dữ liệu:
  - Pin: `GetSystemPowerStatus`.
  - Reboot pending: các khoá `...\Component Based Servicing\RebootPending`, `...\WindowsUpdate\Auto Update\RebootRequired`, giá trị `PendingFileRenameOperations`.
  - Pagefile: `Win32_PageFileUsage`.
  - BitLocker: `root\cimv2\Security\MicrosoftVolumeEncryption` `Win32_EncryptableVolume.ConversionStatus` (cần admin; không admin thì `"Unknown"`, và mức chặn áp dụng như BitLocker).
  - BCD: `bcdedit /enum osloader` đếm khối (cần admin).
  - chkdsk: `chkdsk X:` không tham số, lấy mã thoát.
  - Min size: `MSFT_Partition.GetSupportedSize`.
  - Lock holders: Restart Manager với danh sách file đang mở thu được qua `RmRegisterResources` trên các file tìm thấy. Nếu volume khoá thử (`dmw_volume_lock` rồi `close`) thành công thì danh sách rỗng.
  - `JournalExists`: `File.Exists` của đường dẫn nhật ký.

- [ ] **Step 1: Viết kiểm thử** (chỉ đọc, chạy trên máy thật): `Collect_OnDevMachine_ReportsKnownFacts` → `PagefilePaths` chứa `C:\pagefile.sys`; `OnBattery` khớp `SystemInformation.PowerStatus`; không ném ngoại lệ khi không có quyền admin. `GetHolders_DetectsOpenFile`: mở một file tạm trên ổ của file tạm với `FileShare.None` trong tiến trình con → `GetHolders` trả về tiến trình đó.
- [ ] **Step 2: Chạy** → Expected: FAIL.
- [ ] **Step 3: Cài đặt.**
- [ ] **Step 4: Chạy** → Expected: PASS.
- [ ] **Step 5: Commit** `[Add] System facts probe and Restart Manager helper`

---

### Task 11: Điều phối bước 0–7 và chạy tiếp

**Files:**
- Create: `Services/DiskSpace/DiskMoveNative.cs`, `IDiskMoveBackend.cs`, `WindowsDiskMoveBackend.cs`, `DiskMoveRunner.cs`, `RestoreState.cs`
- Test: `tests/WinOptimizer.Tests/DiskMoveRunnerTests.cs`, `tests/WinOptimizer.Tests/Fakes/FakeDiskMoveBackend.cs`, `tests/WinOptimizer.Tests/IntegrationTests/RunnerVhdxTests.cs`

**Interfaces:**
- Consumes: `MovePlan`, `DiskSnapshot` (Task 8–9); `dmw_*` (Task 6).
- Produces:

```csharp
public enum MoveStep { Checks = 0, ShrinkSource = 1, LockSource = 2, MoveSource = 3, CommitSource = 4, MoveRecovery = 5, ExtendTarget = 6, Verify = 7, Done = 8 }
public sealed record RestoreState(Guid DiskGuid, Guid SourceGuid, char SourceLetter, ulong SourceGptAttributes,
    Guid? RecoveryGuid, bool WinReWasEnabled, Guid TargetGuid, long Amount, int Version = 1);   // JSON → app blob
public sealed record RunProgress(MoveStep Step, long BytesDone, long BytesTotal, string Message);
public sealed record NativeMovePlan(Guid DiskGuid, Guid PartitionGuid, int SectorSize, long OldStart, long Length,
    long Shift, IReadOnlyList<(long Offset, long Length)> Extents);              // ánh xạ sang dm_move_plan
public sealed record JournalInfo(int MoveCount, MoveStep Step, int MoveIndex, long ChunksDone, long[] ChunkCount);
public interface IDiskMoveBackend {
    int  FindDiskNumber(Guid diskGuid);                                 // ném nếu không thấy
    void ResizePartition(int disk, Guid part, long newSize);           // idempotent
    void SetPartitionHidden(int disk, Guid part, bool hidden);
    void RemoveLetter(int disk, Guid part);  void AssignLetter(int disk, Guid part, char letter);
    IntPtr LockVolume(Guid part);  void Dismount(IntPtr vol);  void Unlock(IntPtr vol);
    IReadOnlyList<(long Offset, long Length)> UsedExtents(IntPtr vol, out long usedBytes);
    bool? WinReEnabled(); void SetWinRe(bool enabled);                  // reagentc /info, /enable, /disable
    void RecreateRecoveryPartition(int disk, long offset, long size);   // dự phòng KB5028997
    byte[] ReadRawGpt(int disk);                                        // 34 sector đầu + 33 sector cuối
    void CreateJournal(string path, IReadOnlyList<NativeMovePlan> moves, RestoreState state);
    JournalInfo? ReadJournal(string path, out RestoreState? state);     // null nếu không có file
    void SetJournalStep(string path, MoveStep step);
    void RunMove(string disk, string journal, int moveIndex, Func<long,long,bool> progress); // false = dừng
    void SetPartitionStart(int disk, Guid part, long newStart);  void PatchNtfsHiddenSectors(int disk, long start, long length, int sector);
    int  Chkdsk(char letter);
    long PartitionSize(int disk, Guid part);  long PartitionStart(int disk, Guid part);
}
public sealed class DiskMoveRunner {
    public DiskMoveRunner(IDiskMoveBackend backend, string journalPath, bool manageWinRe = true);
    public Task RunAsync(DiskSnapshot disk, PartitionSnapshot target, PartitionSnapshot source, MovePlan plan,
                         Func<IReadOnlyList<CheckResult>> recheck, IProgress<RunProgress> progress,
                         Func<bool> shouldPause, CancellationToken cancelBeforeMove);
    public Task ResumeAsync(IProgress<RunProgress> progress, Func<bool> shouldPause);
    public static bool HasUnfinishedJournal(string journalPath);
}
```

- Bước 0 lưu `layout-<yyyyMMdd-HHmmss>.json` (bố cục) và `gpt-<yyyyMMdd-HHmmss>.bin` (`ReadRawGpt`) vào thư mục dữ liệu.
- `RunAsync` nhận thêm `Func<IReadOnlyList<CheckResult>> recheck`; gọi nó **ngay trước bước 2**. Có `Blocker` hoặc `FixFirst` thì dừng và hoàn tác bước 1 (mở rộng nguồn về kích thước cũ), ném `DiskMoveAbortedException` kèm danh sách kết quả.
- Thứ tự trong bước 2 (theo spec): khoá → lấy vùng đã dùng → **tạo nhật ký** (chứa `RestoreState` và kế hoạch cả hai phép dời) → ẩn và gỡ chữ cái → tháo.
- Phép dời Recovery dùng một vùng duy nhất `(0, Length)`: chép toàn bộ phân vùng, không đọc bản đồ cluster.
- Khi chạy tiếp, trước khi làm bất cứ gì: vị trí bắt đầu thật của nguồn và Recovery phải bằng **đúng một** trong hai giá trị "cũ" / "mới" của bước ghi trong nhật ký. Nếu không, ném `DiskMoveStateMismatchException`, không thay đổi gì.
- Bước 1, 4, 6 đọc vị trí và kích thước thật trước khi làm (idempotent).
- `ResumeAsync` tìm ổ bằng `FindDiskNumber(state.DiskGuid)`.
- `cancelBeforeMove` chỉ có hiệu lực trước bước 3. Hủy ở bước 1–2 thì hoàn tác: bỏ ẩn, gán lại chữ, mở rộng nguồn về kích thước cũ, xoá nhật ký.
- Bước 5 khi `manageWinRe`: ghi nhận trạng thái, `SetWinRe(false)`, dời, `SetWinRe(state.WinReWasEnabled)`. Nếu bật lại thất bại: tạo lại Recovery theo KB5028997 (xoá phân vùng Recovery đã dời, tạo phân vùng mới cùng kích thước, đặt `TypeGuid` Recovery và thuộc tính `0x8000000000000001`, format NTFS, `reagentc /enable`).
- `shouldPause` được gọi trong callback tiến độ; trả `true` → lõi dừng giữa hai khối (`DM_E_STOPPED`); runner đợi đến khi `shouldPause()` là `false` rồi chạy tiếp.
- Bước 7: `Chkdsk(letter) == 0`, WinRE đúng trạng thái ban đầu, kích thước đúng kế hoạch → xoá nhật ký; giữ bản sao bảng phân vùng (lưu ở bước 0 thành `layout-<yyyyMMdd-HHmmss>.json`) và xoá các bản cũ hơn 30 ngày.

- [ ] **Step 1: Viết kiểm thử hỏng** `DiskMoveRunnerTests` với `FakeDiskMoveBackend` (giữ trạng thái giả của ổ và ghi lại các lệnh gọi):
  - `Run_CallsStepsInOrder`: thứ tự lệnh gọi khớp bước 0–7; ở bước 2, `CreateJournal` xảy ra **trước** `SetPartitionHidden`.
  - `Run_CrashAfterEachStep_ResumeCompletes`: với mỗi bước k = 2…7, giả lập ném lỗi sau bước k → `HasUnfinishedJournal` true → runner mới `ResumeAsync` → trạng thái cuối giống chạy sạch, không bước nào làm hai lần ngoài các bước idempotent.
  - `Resume_FindsDiskByGuidWhenNumberChanged`: giữa hai lần chạy đổi số ổ 0 → 2 → các lệnh sau khi chạy tiếp dùng ổ 2.
  - `Cancel_DuringLock_RestoresSource`: hủy ở bước 2 → nguồn được bỏ ẩn, gán lại chữ cũ, về kích thước ban đầu, nhật ký bị xoá.
  - `WinReFallback_WhenEnableFails`: `SetWinRe(true)` ném lỗi → có lệnh tạo lại Recovery và gọi `SetWinRe(true)` lần hai.
  - `Pause_WaitsAndContinues`: `shouldPause` true trong 3 lần gọi → `RunMove` được gọi lại và hoàn tất.
  - `Run_RecheckBeforeLock_AbortsAndUndoesShrink`: `recheck` trả một `FixFirst` → `DiskMoveAbortedException`, nguồn về kích thước cũ, không có nhật ký, không gọi `LockVolume`.
  - `Resume_StopsWhenLayoutUnexpected`: vị trí nguồn thật khác cả "cũ" lẫn "mới" → `DiskMoveStateMismatchException`, không lệnh ghi nào được gọi.
  - `Run_SavesLayoutAndRawGpt`: sau bước 0 có đủ hai file `layout-*.json` và `gpt-*.bin`.
- [ ] **Step 2: Chạy** → Expected: FAIL.
- [ ] **Step 3: Cài đặt** `DiskMoveRunner`, `RestoreState`, `DiskMoveNative` (P/Invoke `CallingConvention.Cdecl`, `CharSet.Unicode`), `WindowsDiskMoveBackend` (WMI `MSFT_Partition.Resize`, `SetAttributes`/`IsHidden`, `AddAccessPath`/`RemoveAccessPath`; `reagentc` qua `Process`).
- [ ] **Step 4: Chạy** các kiểm thử logic → Expected: PASS.
- [ ] **Step 5: Viết và chạy** `RunnerVhdxTests.FullRun_OnVhdx` (quyền admin, `manageWinRe:false`, đích là C-like của VHDX): bước 1–7 thật → C-like to thêm đúng X, D-like giữ chữ cái, GUID và serial, mọi SHA-256 khớp, `Chkdsk==0`, phân vùng Recovery-like nằm ngay sau C-like. Expected: PASS (đọc `.trx` như Task 7).
- [ ] **Step 6: Commit** `[Add] Disk move runner with journaled steps and resume`

---

### Task 12: Trang Disk Space, màn hình Resume, bảo vệ khi đang chạy

**Files:**
- Create: `Pages/DiskSpacePage.xaml`, `Pages/DiskSpacePage.xaml.cs`, `Services/DiskSpace/PowerGuard.cs`
- Modify: `MainWindow.xaml.cs` (mục điều hướng giữa *Junk Cleaner* và *Startup Apps*, icon `\uEDA2`; khoá thanh bên; trong `OnClosing`, khi đang chuyển thì hiện thông báo và hủy đóng **trước** logic "Minimize to tray on close"), `App.xaml.cs` (khởi động thấy nhật ký dang dở → mở trang Disk Space ở chế độ Resume; `ExitApp` từ chối thoát khi đang chuyển, vì nó cũng được gọi từ mục **Exit** của `Services/TrayIcon.cs` và menu ⚡ của thanh taskbar dọc), `Pages/HomePage.xaml(.cs)` (ô lối tắt "Disk Space"), `README.md` (thêm tính năng, rủi ro, yêu cầu C++ khi build)
- Test: `tests/WinOptimizer.Tests/PowerGuardTests.cs`

**Interfaces:**
- Consumes: `DiskLayoutService`, `SystemFactsProbe`, `DiskMovePrechecks`, `DiskMovePlanner`, `DiskMoveRunner`, `RestartManagerHelper`.
- Produces:
  - `PowerGuard : IDisposable`: `SetThreadExecutionState(ES_CONTINUOUS|ES_SYSTEM_REQUIRED|ES_DISPLAY_REQUIRED|ES_AWAYMODE_REQUIRED)` và `ShutdownBlockReasonCreate(hwnd, "WinOptimizer is moving disk space — please wait.")`; `Dispose` gỡ cả hai.
  - `PowerGuard.ShouldPause()`: `true` khi đang chạy pin và pin < 20%.
  - `MainWindow.SetNavigationLocked(bool)`.

Nội dung trang theo spec mục 7: các bước ① Disk map (thanh tỉ lệ cho mỗi ổ, tô phần đã dùng, chọn nguồn, nút **Analyze**) → ② Analysis (🔴🟡🟢 với nút **Close for me**, **Check again**) → ③ Amount (thanh kéo và ô GB đồng bộ, xem trước Before/After, thời gian ước tính = `usedBytes / 400 MB/s` cho SSD, `/ 80 MB/s` cho HDD theo `MSFT_PhysicalDisk.MediaType`) → ④ Confirm (ô *"I have backed up important data on D:"*, dòng in đậm về ranh giới không thể hủy, nút **Start moving**) → ⑤ Progress/Result (8 bước, thanh tiến độ, tốc độ, thời gian còn lại; **Cancel** chỉ trước bước 3; nút **Open Disk Management**; khi lỗi hiện đường dẫn nhật ký và bản sao bố cục). Việc chuyển chạy bằng `Task.Run` (ưu tiên **bình thường**, không dùng `LowPriority`). Trong lúc chạy, nếu đang dùng pin thì hiện banner đỏ *"Plug in your charger — the move pauses safely below 20% battery."* Banner Resume: *"The last disk space move was interrupted at step N (block a of b). Your data is safe — click Continue to finish."* Ở Limited mode: bản đồ chỉ đọc và nút **Restart as administrator** (ẩn khi đang chạy).

- [ ] **Step 1: Viết kiểm thử hỏng** `PowerGuardTests.ShouldPause_OnlyOnBatteryBelow20` (dùng một hàm đọc trạng thái pin có thể thay thế).
- [ ] **Step 2: Chạy** → FAIL. **Step 3:** Cài đặt `PowerGuard`. **Step 4:** Chạy → PASS.
- [ ] **Step 5: Cài đặt trang và các thay đổi điều hướng.**
- [ ] **Step 6: Kiểm tra trên máy thật — CHỈ ĐỌC.** Chạy app (không admin rồi có admin), mở Disk Space, bấm **Analyze** với nguồn D:, chụp màn hình từng bước ①–④. **Không bấm Start moving.** Expected: bản đồ đúng bố cục EFI/MSR/C/Recovery/D; không có 🔴 trừ khi máy thật sự có vấn đề; lượng tối đa khớp công thức.
- [ ] **Step 7: Kiểm tra trên VHDX:** thêm tuỳ chọn ẩn `--disk-move-test-target <diskNumber>` **chỉ có trong bản Debug** (`#if DEBUG`) cho phép chọn ổ VHDX làm đích (C-like) với `manageWinRe:false`. Chạy trọn luồng giao diện trên VHDX, kể cả hủy ở bước 2 và giết app ở bước 3 rồi mở lại → banner Resume → Continue → hoàn tất.
- [ ] **Step 8: Commit** `[Add] Disk Space page with resume screen and power guard`

---

### Task 13: Kiểm thử trọn luồng trên máy ảo (lớp 4)

**Files:**
- Create: `docs/superpowers/specs/2026-10-27-disk-space-mover-vm-test.md` (danh sách kiểm tra + kết quả)

Điều kiện: chủ dự án bật Hyper-V và cung cấp ISO Windows 11. Máy ảo: Gen 2, 1 ổ 128 GB, cài Windows rồi tạo bố cục C (80 GB) + Recovery + D (40 GB, đổ 15 GB dữ liệu và ghi SHA-256).

- [ ] **Step 1:** Cài bản Release của WinOptimizer vào máy ảo; chạy trọn luồng chuyển 10 GB → Expected: Windows khởi động lại bình thường; `reagentc /info` cho thấy WinRE **Enabled**; D: giữ chữ cái, GUID và serial; SHA-256 khớp; C: to thêm 10 GB.
- [ ] **Step 2:** Lặp lại, tắt nguồn máy ảo (`Stop-VM -TurnOff`) khi đang ở bước 3 → khởi động → mở WinOptimizer → banner Resume → Continue → các kiểm tra như Step 1 đều đạt.
- [ ] **Step 3:** Lặp lại với tắt nguồn ở bước 5 → như Step 2.
- [ ] **Step 4:** Ghi kết quả vào file kiểm tra; commit `[Add] Disk Space Mover VM end-to-end test results`.
- [ ] **Step 5:** Báo chủ dự án. Việc chạy trên máy thật do chủ dự án quyết định (spec mục 9).
