/* GPT partition move (IOCTL_DISK_*_DRIVE_LAYOUT_EX) and the NTFS HiddenSectors patch. */
#include "dmw_internal.h"
#include <stdlib.h>
#include <string.h>

dm_status dmw_layout_move_partition(DRIVE_LAYOUT_INFORMATION_EX *layout, const uint8_t partition_guid[16],
                                    uint64_t new_start, uint32_t sector_size, int *changed)
{
    if (!layout || !partition_guid || !changed) return DM_E_INVALID_ARG;
    *changed = 0;
    if (layout->PartitionStyle != PARTITION_STYLE_GPT) return DM_E_INVALID_ARG;

    PARTITION_INFORMATION_EX *e = layout->PartitionEntry, *target = NULL;
    for (DWORD i = 0; i < layout->PartitionCount; i++)
        if (memcmp(&e[i].Gpt.PartitionId, partition_guid, sizeof(GUID)) == 0) { target = &e[i]; break; }
    if (!target) return DM_E_NOT_FOUND;
    if ((uint64_t)target->StartingOffset.QuadPart == new_start) return DM_OK;   /* already moved */

    uint64_t len = (uint64_t)target->PartitionLength.QuadPart;
    if (sector_size && new_start % sector_size) return DM_E_INVALID_ARG;
    if (len == 0 || new_start > UINT64_MAX - len) return DM_E_INVALID_ARG;
    uint64_t end = new_start + len;
    uint64_t lo = (uint64_t)layout->Gpt.StartingUsableOffset.QuadPart;
    uint64_t hi = lo + (uint64_t)layout->Gpt.UsableLength.QuadPart;
    if (new_start < lo || end > hi) return DM_E_INVALID_ARG;
    for (DWORD i = 0; i < layout->PartitionCount; i++) {
        if (&e[i] == target || e[i].PartitionLength.QuadPart <= 0) continue;
        uint64_t os = (uint64_t)e[i].StartingOffset.QuadPart, ol = (uint64_t)e[i].PartitionLength.QuadPart;
        uint64_t oe = os > UINT64_MAX - ol ? UINT64_MAX : os + ol;
        if (new_start < oe && os < end) return DM_E_INVALID_ARG;
    }

    target->StartingOffset.QuadPart = (LONGLONG)new_start;
    target->RewritePartition = TRUE;
    *changed = 1;
    return DM_OK;
}

dm_status dmw_layout_set_attributes(DRIVE_LAYOUT_INFORMATION_EX *layout, const uint8_t partition_guid[16],
                                     uint64_t attributes, uint64_t *old_attributes, int *changed)
{
    if (!layout || !partition_guid || !changed) return DM_E_INVALID_ARG;
    *changed = 0;
    if (layout->PartitionStyle != PARTITION_STYLE_GPT) return DM_E_INVALID_ARG;
    PARTITION_INFORMATION_EX *target = NULL;
    for (DWORD i = 0; i < layout->PartitionCount; i++)
        if (memcmp(&layout->PartitionEntry[i].Gpt.PartitionId, partition_guid, sizeof(GUID)) == 0) {
            target = &layout->PartitionEntry[i];
            break;
        }
    if (!target) return DM_E_NOT_FOUND;
    if (old_attributes) *old_attributes = target->Gpt.Attributes;
    if (target->Gpt.Attributes == attributes) return DM_OK;   /* already set */
    target->Gpt.Attributes = attributes;
    target->RewritePartition = TRUE;
    *changed = 1;
    return DM_OK;
}

dm_status dmw_layout_get_attributes(const DRIVE_LAYOUT_INFORMATION_EX *layout, const uint8_t partition_guid[16],
                                    uint64_t *attributes)
{
    if (!layout || !partition_guid || !attributes) return DM_E_INVALID_ARG;
    if (layout->PartitionStyle != PARTITION_STYLE_GPT) return DM_E_INVALID_ARG;
    for (DWORD i = 0; i < layout->PartitionCount; i++)
        if (memcmp(&layout->PartitionEntry[i].Gpt.PartitionId, partition_guid, sizeof(GUID)) == 0) {
            *attributes = layout->PartitionEntry[i].Gpt.Attributes;
            return DM_OK;
        }
    return DM_E_NOT_FOUND;
}

dm_status dmw_layout_check_move(const DRIVE_LAYOUT_INFORMATION_EX *layout, const dm_move_plan *plan)
{
    if (!layout || !plan) return DM_E_INVALID_ARG;
    if (layout->PartitionStyle != PARTITION_STYLE_GPT ||
        memcmp(&layout->Gpt.DiskId, plan->disk_guid, sizeof(GUID)) != 0)
        return DM_E_JOURNAL_MISMATCH;
    if (plan->length == 0 || plan->old_start > UINT64_MAX - plan->shift ||
        plan->old_start + plan->shift > UINT64_MAX - plan->length)
        return DM_E_JOURNAL_MISMATCH;
    uint64_t dst = plan->old_start + plan->shift, dst_end = dst + plan->length;

    /* The destination must lie inside the GPT usable range. */
    uint64_t lo = (uint64_t)layout->Gpt.StartingUsableOffset.QuadPart;
    uint64_t ul = (uint64_t)layout->Gpt.UsableLength.QuadPart;
    uint64_t hi = lo > UINT64_MAX - ul ? UINT64_MAX : lo + ul;
    if (dst < lo || dst_end > hi) return DM_E_JOURNAL_MISMATCH;

    const PARTITION_INFORMATION_EX *self = NULL;
    for (DWORD i = 0; i < layout->PartitionCount; i++) {
        const PARTITION_INFORMATION_EX *e = &layout->PartitionEntry[i];
        if (e->PartitionStyle == PARTITION_STYLE_GPT &&
            memcmp(&e->Gpt.PartitionId, plan->partition_guid, sizeof(GUID)) == 0) {
            self = e;
            break;
        }
    }
    if (!self) return DM_E_JOURNAL_MISMATCH;
    uint64_t start = (uint64_t)self->StartingOffset.QuadPart;
    if ((uint64_t)self->PartitionLength.QuadPart != plan->length) return DM_E_JOURNAL_MISMATCH;
    if (start != plan->old_start && start != dst) return DM_E_JOURNAL_MISMATCH;

    /* Defence in depth: the raw writes go to [dst, dst_end); no OTHER partition may own any of it (the moved
       partition may overlap its own old range). A legitimate source move lands inside the source's original
       extent and a Recovery move inside the space the source freed. */
    for (DWORD i = 0; i < layout->PartitionCount; i++) {
        const PARTITION_INFORMATION_EX *e = &layout->PartitionEntry[i];
        if (e == self || e->PartitionLength.QuadPart <= 0) continue;
        uint64_t os = (uint64_t)e->StartingOffset.QuadPart, ol = (uint64_t)e->PartitionLength.QuadPart;
        uint64_t oe = os > UINT64_MAX - ol ? UINT64_MAX : os + ol;
        if (dst < oe && os < dst_end) return DM_E_JOURNAL_MISMATCH;
    }
    return DM_OK;
}

/* EFI system partition type c12a7328-f81f-11d2-ba4b-00a0c93ec93b. */
static const GUID k_esp_type = { 0xC12A7328, 0xF81F, 0x11D2, { 0xBA, 0x4B, 0x00, 0xA0, 0xC9, 0x3E, 0xC9, 0x3B } };

int dmw_layout_has_system_partition(const DRIVE_LAYOUT_INFORMATION_EX *layout)
{
    if (!layout) return 0;
    for (DWORD i = 0; i < layout->PartitionCount; i++) {
        const PARTITION_INFORMATION_EX *e = &layout->PartitionEntry[i];
        if (layout->PartitionStyle == PARTITION_STYLE_GPT && e->PartitionStyle == PARTITION_STYLE_GPT &&
            memcmp(&e->Gpt.PartitionType, &k_esp_type, sizeof(GUID)) == 0)
            return 1;                                       /* UEFI: ESP */
        if (layout->PartitionStyle == PARTITION_STYLE_MBR && e->PartitionStyle == PARTITION_STYLE_MBR &&
            (e->Mbr.BootIndicator || e->Mbr.PartitionType == 0xEF))
            return 1;                                       /* BIOS: active partition, or MBR ESP */
    }
    return 0;
}

static dm_status ioctl(HANDLE h, DWORD code, void *in, DWORD in_len, void *out, DWORD out_len)
{
    DWORD ret = 0;
    if (!DeviceIoControl(h, code, in, in_len, out, out_len, &ret, NULL)) return dmw_status_from_win32(GetLastError());
    return DM_OK;
}

/* IOCTL_DISK_GET_DRIVE_LAYOUT_EX into a malloc'd buffer (grown on ERROR_INSUFFICIENT_BUFFER). */
static dm_status read_layout(HANDLE h, DRIVE_LAYOUT_INFORMATION_EX **out)
{
    *out = NULL;
    DWORD cap = sizeof(DRIVE_LAYOUT_INFORMATION_EX) + 127 * sizeof(PARTITION_INFORMATION_EX);
    for (;;) {
        DRIVE_LAYOUT_INFORMATION_EX *lay = (DRIVE_LAYOUT_INFORMATION_EX *)malloc(cap);
        if (!lay) return DM_E_NOMEM;
        DWORD ret = 0;
        if (DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_LAYOUT_EX, NULL, 0, lay, cap, &ret, NULL)) {
            *out = lay;
            return DM_OK;
        }
        DWORD err = GetLastError();
        free(lay);
        if (err != ERROR_INSUFFICIENT_BUFFER || cap > (1u << 24)) return dmw_status_from_win32(err);
        cap *= 2;
    }
}

/* One layout edit: GET_DRIVE_LAYOUT_EX -> edit -> SET_DRIVE_LAYOUT_EX (only if changed) ->
   IOCTL_DISK_UPDATE_PROPERTIES (always, so a retry after a failed refresh still refreshes). */
typedef dm_status (*layout_edit_fn)(DRIVE_LAYOUT_INFORMATION_EX *lay, uint32_t sector_size, void *ctx, int *changed);

static dm_status edit_layout(const wchar_t *disk_path, layout_edit_fn edit, void *ctx)
{
    HANDLE h = CreateFileW(disk_path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                           OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return dmw_status_from_win32(GetLastError());

    DISK_GEOMETRY_EX geo;
    DRIVE_LAYOUT_INFORMATION_EX *lay = NULL;
    dm_status s = ioctl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, NULL, 0, &geo, sizeof geo);
    if (s == DM_OK) s = read_layout(h, &lay);

    int changed = 0;
    if (s == DM_OK) s = edit(lay, geo.Geometry.BytesPerSector, ctx, &changed);
    if (s == DM_OK && changed) {
        DWORD in_len = (DWORD)(FIELD_OFFSET(DRIVE_LAYOUT_INFORMATION_EX, PartitionEntry) +
                               lay->PartitionCount * sizeof(PARTITION_INFORMATION_EX));
        s = ioctl(h, IOCTL_DISK_SET_DRIVE_LAYOUT_EX, lay, in_len, NULL, 0);
    }
    if (s == DM_OK) s = ioctl(h, IOCTL_DISK_UPDATE_PROPERTIES, NULL, 0, NULL, 0);
    free(lay);
    CloseHandle(h);
    return s;
}

/* Read-only layout query (GENERIC_READ only). */
dm_status dmw_query_layout(const wchar_t *disk_path, DRIVE_LAYOUT_INFORMATION_EX **out)
{
    HANDLE h = CreateFileW(disk_path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return dmw_status_from_win32(GetLastError());
    dm_status s = read_layout(h, out);
    CloseHandle(h);
    return s;
}

typedef struct move_ctx { const uint8_t *guid; uint64_t new_start; } move_ctx;

static dm_status edit_move(DRIVE_LAYOUT_INFORMATION_EX *lay, uint32_t sector_size, void *ctx, int *changed)
{
    move_ctx *m = (move_ctx *)ctx;
    return dmw_layout_move_partition(lay, m->guid, m->new_start, sector_size, changed);
}

DMW_API dm_status DMW_CALL dmw_set_partition_start(const wchar_t *disk_path, const uint8_t partition_guid[16],
                                                   uint64_t new_start)
{
    if (!disk_path || !disk_path[0] || !partition_guid) return DM_E_INVALID_ARG;
    move_ctx m = { partition_guid, new_start };
    return edit_layout(disk_path, edit_move, &m);
}

typedef struct attr_ctx { const uint8_t *guid; uint64_t attributes, old; } attr_ctx;

static dm_status edit_attributes(DRIVE_LAYOUT_INFORMATION_EX *lay, uint32_t sector_size, void *ctx, int *changed)
{
    (void)sector_size;
    attr_ctx *a = (attr_ctx *)ctx;
    return dmw_layout_set_attributes(lay, a->guid, a->attributes, &a->old, changed);
}

DMW_API dm_status DMW_CALL dmw_set_partition_attributes(const wchar_t *disk_path, const uint8_t partition_guid[16],
                                                        uint64_t attributes, uint64_t *old_attributes)
{
    if (!disk_path || !disk_path[0] || !partition_guid) return DM_E_INVALID_ARG;
    attr_ctx a = { partition_guid, attributes, 0 };
    dm_status s = edit_layout(disk_path, edit_attributes, &a);
    if (s == DM_OK && old_attributes) *old_attributes = a.old;
    return s;
}


#define BOOT_WINDOW 4096u

/* Patches one boot sector at absolute offset off. Unbuffered I/O needs sector-aligned windows: use a
   4096-byte window when it stays inside the partition (safe on 512e/4Kn hosts), else one sector. */
static dm_status patch_one(dm_io *io, uint8_t *buf, uint64_t start, uint64_t length, uint64_t off,
                           uint32_t sector_size, uint32_t hidden)
{
    uint64_t base = off & ~(uint64_t)(BOOT_WINDOW - 1);
    uint32_t wlen = BOOT_WINDOW;
    if (base < start || base + BOOT_WINDOW > start + length) { base = off; wlen = sector_size; }

    dm_status s = io->read(io->ctx, base, buf, wlen);
    if (s != DM_OK) return s;
    uint8_t *sec = buf + (off - base);
    if (memcmp(sec + 3, "NTFS    ", 8) != 0) return DM_OK;   /* not an NTFS boot sector: leave it */
    uint8_t v[4] = { (uint8_t)hidden, (uint8_t)(hidden >> 8), (uint8_t)(hidden >> 16), (uint8_t)(hidden >> 24) };
    if (memcmp(sec + 0x1C, v, 4) == 0) return DM_OK;          /* already patched */
    memcpy(sec + 0x1C, v, 4);
    s = io->write(io->ctx, base, buf, wlen);
    if (s == DM_OK) s = io->flush(io->ctx);
    return s;
}

DMW_API dm_status DMW_CALL dmw_patch_ntfs_hidden_sectors(const wchar_t *disk_path, uint64_t start, uint64_t length,
                                                         uint32_t sector_size)
{
    if (!disk_path || !disk_path[0]) return DM_E_INVALID_ARG;
    if (sector_size < 512 || sector_size > BOOT_WINDOW || (sector_size & (sector_size - 1))) return DM_E_INVALID_ARG;
    if (start % sector_size || length % sector_size || length < sector_size || start > UINT64_MAX - length)
        return DM_E_INVALID_ARG;
    if (start / sector_size > UINT32_MAX) return DM_OK;   /* the 32-bit field cannot hold it: leave it */
    uint32_t hidden = (uint32_t)(start / sector_size);

    dmw_file f;
    dm_status s = dmw_disk_open(disk_path, &f);
    if (s != DM_OK) return s;
    uint8_t *buf = (uint8_t *)VirtualAlloc(NULL, BOOT_WINDOW, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!buf) {
        dmw_file_close(&f);
        return DM_E_NOMEM;
    }
    dm_io io = dmw_file_as_dm_io(&f);
    uint64_t backup = start + length - sector_size;
    s = patch_one(&io, buf, start, length, start, sector_size, hidden);
    if (s == DM_OK && backup != start) s = patch_one(&io, buf, start, length, backup, sector_size, hidden);
    VirtualFree(buf, 0, MEM_RELEASE);
    dmw_file_close(&f);
    return s;
}

DMW_API dm_status DMW_CALL dmw_get_partition_attributes(const wchar_t *disk_path, const uint8_t partition_guid[16],
                                                        uint64_t *attributes)
{
    if (!disk_path || !disk_path[0] || !partition_guid || !attributes) return DM_E_INVALID_ARG;
    DRIVE_LAYOUT_INFORMATION_EX *lay = NULL;
    dm_status s = dmw_query_layout(disk_path, &lay);
    if (s == DM_OK) s = dmw_layout_get_attributes(lay, partition_guid, attributes);
    free(lay);
    return s;
}

/* Disk extents of the volume holding the Windows directory contain disk_number? */
static dm_status windows_volume_on_disk(uint32_t disk_number, int *hit)
{
    wchar_t win[MAX_PATH], mount[MAX_PATH], vol[MAX_PATH];
    UINT n = GetSystemWindowsDirectoryW(win, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return DM_E_IO;
    if (!GetVolumePathNameW(win, mount, MAX_PATH)) return dmw_status_from_win32(GetLastError());
    if (!GetVolumeNameForVolumeMountPointW(mount, vol, MAX_PATH)) return dmw_status_from_win32(GetLastError());
    size_t len = wcslen(vol);
    if (len && vol[len - 1] == L'\\') vol[len - 1] = 0;   /* volume device, not its root directory */

    /* Query only: no data access, nothing is locked or written. */
    HANDLE h = CreateFileW(vol, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return dmw_status_from_win32(GetLastError());
    union { VOLUME_DISK_EXTENTS e; uint8_t raw[sizeof(VOLUME_DISK_EXTENTS) + 31 * sizeof(DISK_EXTENT)]; } buf;
    DWORD ret = 0;
    dm_status s = DM_OK;
    if (!DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, NULL, 0, &buf, sizeof buf, &ret, NULL))
        s = dmw_status_from_win32(GetLastError());
    CloseHandle(h);
    if (s != DM_OK) return s;
    *hit = 0;
    for (DWORD i = 0; i < buf.e.NumberOfDiskExtents && i < 32; i++)
        if (buf.e.Extents[i].DiskNumber == disk_number) *hit = 1;
    return DM_OK;
}

DMW_API dm_status DMW_CALL dmw_disk_is_system(uint32_t disk_number, int *is_system)
{
    if (!is_system) return DM_E_INVALID_ARG;
    *is_system = 1;   /* fail closed: callers refuse on any error */
    int hit = 0;
    dm_status s = windows_volume_on_disk(disk_number, &hit);
    if (s != DM_OK || hit) return s;

    /* Boot side: an ESP (GPT) or active/ESP partition (MBR) anywhere on the disk. Needs read access. */
    wchar_t path[64];
    swprintf(path, 64, L"\\\\.\\PhysicalDrive%u", disk_number);
    DRIVE_LAYOUT_INFORMATION_EX *lay = NULL;
    s = dmw_query_layout(path, &lay);
    if (s == DM_OK) *is_system = dmw_layout_has_system_partition(lay);
    free(lay);
    return s;
}
