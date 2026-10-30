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

/* ---- old-offset volume of a half-moved partition ---- */

typedef union extents_buf {
    VOLUME_DISK_EXTENTS e;
    uint8_t raw[sizeof(VOLUME_DISK_EXTENTS) + 7 * sizeof(DISK_EXTENT)];
} extents_buf;

static void volume_device_path(uint32_t n, wchar_t *out, size_t cap)
{
    swprintf(out, cap, L"\\\\?\\GLOBALROOT\\Device\\HarddiskVolume%u", n);
}

dm_status __cdecl dmw_query_volume_device(void *ctx, uint32_t n, VOLUME_DISK_EXTENTS *buf, uint32_t size)
{
    (void)ctx;
    wchar_t path[64];
    volume_device_path(n, path, 64);
    /* Zero access: a direct device open, which never mounts a file system. */
    HANDLE h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return dmw_status_from_win32(GetLastError());
    DWORD ret = 0;
    BOOL ok = DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, NULL, 0, buf, size, &ret, NULL);
    DWORD err = ok ? 0 : GetLastError();
    CloseHandle(h);
    if (!ok) return err == ERROR_MORE_DATA ? DM_E_INVALID_ARG : dmw_status_from_win32(err);
    return DM_OK;
}

dm_status dmw_find_volume_device(dmw_extents_query query, void *ctx, uint32_t max, uint32_t disk_number,
                                 uint64_t start, uint64_t length, uint32_t *found)
{
    if (!query || !found) return DM_E_INVALID_ARG;
    for (uint32_t n = 1; n <= max; n++) {
        extents_buf b;
        if (query(ctx, n, &b.e, (uint32_t)sizeof b) != DM_OK) continue;   /* no such device, or not queryable */
        if (dmw_extents_match(&b.e, disk_number, start, length) == DM_OK) {
            *found = n;
            return DM_OK;
        }
    }
    return DM_E_NOT_FOUND;
}

/* One attempt of dmw_lock_old_volume. */
static dm_status lock_old_volume_once(uint32_t disk_number, uint64_t start, uint64_t length, dmw_volume **out)
{
    uint32_t n = 0;
    dm_status s = dmw_find_volume_device(dmw_query_volume_device, NULL, DMW_VOLUME_SCAN_MAX, disk_number, start,
                                         length, &n);
    if (s == DM_E_NOT_FOUND) return DM_OK;   /* no volume device at the old offset: nothing can mount it */
    if (s != DM_OK) return s;

    wchar_t path[64];
    volume_device_path(n, path, 64);
    dmw_volume *v = NULL;
    s = dmw_volume_lock(path, &v);           /* a read/write open: Windows mounts it first (RAW once invalidated) */
    if (s == DM_E_NOT_FOUND) return DM_OK;   /* it went away meanwhile */
    if (s != DM_OK) return DM_E_LOCKED;
    if (dmw_volume_verify(v, disk_number, start, length) != DM_OK || dmw_volume_dismount(v) != DM_OK) {
        dmw_volume_close(v);
        return DM_E_LOCKED;
    }
    *out = v;
    return DM_OK;
}

dm_status dmw_lock_old_volume(uint32_t disk_number, uint64_t start, uint64_t length, dmw_volume **out)
{
    if (!out) return DM_E_INVALID_ARG;
    *out = NULL;
    /* Right after Windows mounted it (RAW), a scanner may hold it for a moment: retry like the app's own lock. */
    dm_status s = DM_E_LOCKED;
    for (int attempt = 0; attempt < DMW_OLD_LOCK_ATTEMPTS; attempt++) {
        if (attempt) Sleep(1000);
        s = lock_old_volume_once(disk_number, start, length, out);
        if (s != DM_E_LOCKED) break;
    }
    return s;
}
