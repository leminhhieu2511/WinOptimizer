/* Volume lock / dismount. Closing the handle releases the lock (and lets Windows remount). */
#include "dmw_internal.h"
#include <stdlib.h>
#include <string.h>

struct dmw_volume { HANDLE h; };

DMW_API dm_status DMW_CALL dmw_volume_lock(const wchar_t *volume_path, dmw_volume **out)
{
    if (!out) return DM_E_INVALID_ARG;
    *out = NULL;
    if (!volume_path || !volume_path[0]) return DM_E_INVALID_ARG;

    /* "\\?\Volume{...}\" names the root directory; the volume device is the path without the slash. */
    size_t n = wcslen(volume_path);
    wchar_t *path = (wchar_t *)malloc((n + 1) * sizeof *path);
    if (!path) return DM_E_NOMEM;
    memcpy(path, volume_path, (n + 1) * sizeof *path);
    if (n > 1 && path[n - 1] == L'\\') path[n - 1] = 0;

    HANDLE h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                           OPEN_EXISTING, 0, NULL);
    free(path);
    if (h == INVALID_HANDLE_VALUE) return dmw_status_from_win32(GetLastError());

    DWORD ret = 0;
    if (!DeviceIoControl(h, FSCTL_LOCK_VOLUME, NULL, 0, NULL, 0, &ret, NULL)) {
        DWORD err = GetLastError();
        CloseHandle(h);
        /* Another process holds files open on the volume. */
        if (err == ERROR_ACCESS_DENIED || err == ERROR_SHARING_VIOLATION) return DM_E_LOCKED;
        return dmw_status_from_win32(err);
    }

    dmw_volume *v = (dmw_volume *)malloc(sizeof *v);
    if (!v) {
        CloseHandle(h);
        return DM_E_NOMEM;
    }
    v->h = h;
    *out = v;
    return DM_OK;
}

DMW_API dm_status DMW_CALL dmw_volume_dismount(dmw_volume *v)
{
    if (!v) return DM_E_INVALID_ARG;
    DWORD ret = 0;
    if (!DeviceIoControl(v->h, FSCTL_DISMOUNT_VOLUME, NULL, 0, NULL, 0, &ret, NULL))
        return dmw_status_from_win32(GetLastError());
    return DM_OK;
}

DMW_API void DMW_CALL dmw_volume_close(dmw_volume *v)
{
    if (!v) return;
    CloseHandle(v->h);
    free(v);
}

dm_status dmw_extents_match(const VOLUME_DISK_EXTENTS *ext, uint32_t disk_number, uint64_t start, uint64_t length)
{
    if (!ext || ext->NumberOfDiskExtents != 1) return DM_E_INVALID_ARG;   /* spanned/striped or empty */
    const DISK_EXTENT *e = &ext->Extents[0];
    if (e->DiskNumber != disk_number || (uint64_t)e->StartingOffset.QuadPart != start ||
        (uint64_t)e->ExtentLength.QuadPart != length)
        return DM_E_INVALID_ARG;
    return DM_OK;
}

DMW_API dm_status DMW_CALL dmw_volume_verify(dmw_volume *v, uint32_t disk_number, uint64_t start, uint64_t length)
{
    if (!v) return DM_E_INVALID_ARG;
    /* Room for a few extents so a multi-extent volume reads fully and is then rejected by the match. */
    union { VOLUME_DISK_EXTENTS e; uint8_t raw[sizeof(VOLUME_DISK_EXTENTS) + 7 * sizeof(DISK_EXTENT)]; } buf;
    DWORD ret = 0;
    if (!DeviceIoControl(v->h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, NULL, 0, &buf, sizeof buf, &ret, NULL)) {
        DWORD err = GetLastError();
        return err == ERROR_MORE_DATA ? DM_E_INVALID_ARG : dmw_status_from_win32(err);
    }
    return dmw_extents_match(&buf.e, disk_number, start, length);
}

DMW_API dm_status DMW_CALL dmw_disk_number(const wchar_t *disk_path, uint32_t *out)
{
    if (!disk_path || !disk_path[0] || !out) return DM_E_INVALID_ARG;
    /* No data access is needed for the query. */
    HANDLE h = CreateFileW(disk_path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return dmw_status_from_win32(GetLastError());
    STORAGE_DEVICE_NUMBER n;
    DWORD ret = 0;
    dm_status s = DM_OK;
    if (!DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, NULL, 0, &n, sizeof n, &ret, NULL))
        s = dmw_status_from_win32(GetLastError());
    else if (n.DeviceType != FILE_DEVICE_DISK || n.PartitionNumber != 0)
        s = DM_E_INVALID_ARG;   /* a volume/partition handle reports PartitionNumber >= 1: not a whole disk */
    else
        *out = n.DeviceNumber;
    CloseHandle(h);
    return s;
}
