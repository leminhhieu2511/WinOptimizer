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
        uint64_t os = (uint64_t)e[i].StartingOffset.QuadPart, oe = os + (uint64_t)e[i].PartitionLength.QuadPart;
        if (new_start < oe && os < end) return DM_E_INVALID_ARG;
    }

    target->StartingOffset.QuadPart = (LONGLONG)new_start;
    target->RewritePartition = TRUE;
    *changed = 1;
    return DM_OK;
}

static dm_status ioctl(HANDLE h, DWORD code, void *in, DWORD in_len, void *out, DWORD out_len)
{
    DWORD ret = 0;
    if (!DeviceIoControl(h, code, in, in_len, out, out_len, &ret, NULL)) return dmw_status_from_win32(GetLastError());
    return DM_OK;
}

DMW_API dm_status DMW_CALL dmw_set_partition_start(const wchar_t *disk_path, const uint8_t partition_guid[16],
                                                   uint64_t new_start)
{
    if (!disk_path || !disk_path[0] || !partition_guid) return DM_E_INVALID_ARG;
    HANDLE h = CreateFileW(disk_path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                           OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return dmw_status_from_win32(GetLastError());

    DISK_GEOMETRY_EX geo;
    dm_status s = ioctl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, NULL, 0, &geo, sizeof geo);

    DRIVE_LAYOUT_INFORMATION_EX *lay = NULL;
    DWORD cap = sizeof(DRIVE_LAYOUT_INFORMATION_EX) + 127 * sizeof(PARTITION_INFORMATION_EX);
    while (s == DM_OK) {
        lay = (DRIVE_LAYOUT_INFORMATION_EX *)malloc(cap);
        if (!lay) { s = DM_E_NOMEM; break; }
        DWORD ret = 0;
        if (DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_LAYOUT_EX, NULL, 0, lay, cap, &ret, NULL)) break;
        DWORD err = GetLastError();
        free(lay);
        lay = NULL;
        if (err != ERROR_INSUFFICIENT_BUFFER || cap > (1u << 24)) { s = dmw_status_from_win32(err); break; }
        cap *= 2;
    }

    int changed = 0;
    if (s == DM_OK) s = dmw_layout_move_partition(lay, partition_guid, new_start, geo.Geometry.BytesPerSector, &changed);
    if (s == DM_OK && changed) {
        DWORD in_len = (DWORD)(FIELD_OFFSET(DRIVE_LAYOUT_INFORMATION_EX, PartitionEntry) +
                               lay->PartitionCount * sizeof(PARTITION_INFORMATION_EX));
        s = ioctl(h, IOCTL_DISK_SET_DRIVE_LAYOUT_EX, lay, in_len, NULL, 0);
        if (s == DM_OK) s = ioctl(h, IOCTL_DISK_UPDATE_PROPERTIES, NULL, 0, NULL, 0);
    }
    free(lay);
    CloseHandle(h);
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
