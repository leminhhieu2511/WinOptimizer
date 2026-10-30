/* Volume lock / dismount. Closing the handle releases the lock (and lets Windows remount). */
#include "dmw_internal.h"
#include <stdlib.h>
#include <string.h>

/* A lock, or a chain of locks (every old-offset device of a partition), closed together. */
struct dmw_volume { HANDLE h; struct dmw_volume *next; };

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
    v->next = NULL;
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
    while (v) {
        dmw_volume *next = v->next;
        CloseHandle(v->h);
        free(v);
        v = next;
    }
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

dm_status dmw_find_volume_devices(dmw_extents_query query, void *ctx, const uint32_t *cand, uint32_t ncand,
                                  uint32_t disk_number, uint64_t start, uint64_t length,
                                  uint32_t *found, uint32_t cap, uint32_t *nfound)
{
    if (nfound) *nfound = 0;
    if (!query || !cand || !found || !nfound) return DM_E_INVALID_ARG;
    for (uint32_t i = 0; i < ncand; i++) {
        extents_buf b;
        if (query(ctx, cand[i], &b.e, (uint32_t)sizeof b) != DM_OK) continue;   /* gone, or not queryable */
        if (dmw_extents_match(&b.e, disk_number, start, length) != DM_OK) continue;
        if (*nfound >= cap) return DM_E_NOMEM;
        found[(*nfound)++] = cand[i];
    }
    return *nfound ? DM_OK : DM_E_NOT_FOUND;
}

int dmw_parse_volume_name(const wchar_t *name, size_t len, uint32_t *n)
{
    static const wchar_t prefix[] = L"HarddiskVolume";
    const size_t pl = sizeof prefix / sizeof prefix[0] - 1;
    if (!name || !n || len <= pl || wcsncmp(name, prefix, pl) != 0) return 0;
    uint64_t v = 0;
    for (size_t i = pl; i < len; i++) {
        if (name[i] < L'0' || name[i] > L'9') return 0;
        v = v * 10 + (uint64_t)(name[i] - L'0');
        if (v > 0xFFFFFFFFull) return 0;
    }
    *n = (uint32_t)v;
    return 1;
}

/* ntdll object-directory API (documented in winternl / the WDK; loaded dynamically). */
typedef struct dmw_unicode { USHORT Length, MaximumLength; PWSTR Buffer; } dmw_unicode;
typedef struct dmw_objattr {
    ULONG Length; HANDLE RootDirectory; dmw_unicode *ObjectName; ULONG Attributes; PVOID Sd, Qos;
} dmw_objattr;
typedef struct dmw_dirinfo { dmw_unicode Name, TypeName; } dmw_dirinfo;
typedef LONG (NTAPI *nt_open_dir)(HANDLE *, ACCESS_MASK, dmw_objattr *);
typedef LONG (NTAPI *nt_query_dir)(HANDLE, PVOID, ULONG, BOOLEAN, BOOLEAN, PULONG, PULONG);
typedef LONG (NTAPI *nt_close)(HANDLE);

static int list_device_directory(uint32_t **out, uint32_t *count)
{
    HMODULE nt = GetModuleHandleW(L"ntdll.dll");
    if (!nt) return 0;
    nt_open_dir open_dir = (nt_open_dir)(void (*)(void))GetProcAddress(nt, "NtOpenDirectoryObject");
    nt_query_dir query_dir = (nt_query_dir)(void (*)(void))GetProcAddress(nt, "NtQueryDirectoryObject");
    nt_close close_h = (nt_close)(void (*)(void))GetProcAddress(nt, "NtClose");
    if (!open_dir || !query_dir || !close_h) return 0;

    wchar_t dev[] = L"\\Device";
    dmw_unicode name = { (USHORT)(7 * sizeof(wchar_t)), (USHORT)sizeof dev, dev };
    dmw_objattr oa = { sizeof oa, NULL, &name, 0x40 /* OBJ_CASE_INSENSITIVE */, NULL, NULL };
    HANDLE dir = NULL;
    if (open_dir(&dir, 0x0001 /* DIRECTORY_QUERY */, &oa) < 0) return 0;

    uint32_t cap = 256, n = 0;
    uint32_t *list = (uint32_t *)malloc(cap * sizeof *list);
    const ULONG bufsz = 64 * 1024;
    uint8_t *buf = (uint8_t *)malloc(bufsz);
    int ok = list && buf;
    ULONG ctx = 0, ret = 0;
    BOOLEAN first = TRUE;
    while (ok) {
        LONG st = query_dir(dir, buf, bufsz, FALSE, first, &ctx, &ret);
        first = FALSE;
        if (st == (LONG)0x8000001A /* STATUS_NO_MORE_ENTRIES */) break;
        if (st < 0) { ok = 0; break; }
        for (const dmw_dirinfo *e = (const dmw_dirinfo *)buf; e->Name.Buffer; e++) {
            uint32_t num;
            if (!dmw_parse_volume_name(e->Name.Buffer, e->Name.Length / sizeof(wchar_t), &num)) continue;
            if (n == cap) {
                uint32_t *bigger = (uint32_t *)realloc(list, (size_t)cap * 2 * sizeof *list);
                if (!bigger) { ok = 0; break; }
                list = bigger;
                cap *= 2;
            }
            list[n++] = num;
        }
        if (st == 0) continue;                  /* STATUS_SUCCESS: maybe more */
        if (st == 0x00000105 /* STATUS_MORE_ENTRIES */) continue;
    }
    close_h(dir);
    free(buf);
    if (!ok || n == 0) { free(list); return 0; }
    *out = list;
    *count = n;
    return 1;
}

dm_status dmw_volume_numbers(uint32_t **out, uint32_t *count, int *from_directory)
{
    if (!out || !count || !from_directory) return DM_E_INVALID_ARG;
    *out = NULL; *count = 0; *from_directory = 0;
    if (list_device_directory(out, count)) {
        *from_directory = 1;
        return DM_OK;
    }
    uint32_t *list = (uint32_t *)malloc(DMW_VOLUME_SCAN_MAX * sizeof *list);
    if (!list) return DM_E_NOMEM;
    for (uint32_t i = 0; i < DMW_VOLUME_SCAN_MAX; i++) list[i] = i + 1;
    *out = list;
    *count = DMW_VOLUME_SCAN_MAX;
    return DM_OK;
}

/* Every device at exactly [start, start + length) on the disk. */
static dm_status find_devices(uint32_t disk_number, uint64_t start, uint64_t length, uint32_t *found, uint32_t *nfound)
{
    uint32_t *cand = NULL, ncand = 0;
    int from_dir = 0;
    dm_status s = dmw_volume_numbers(&cand, &ncand, &from_dir);
    if (s != DM_OK) return s;
    s = dmw_find_volume_devices(dmw_query_volume_device, NULL, cand, ncand, disk_number, start, length, found,
                                DMW_MAX_OLD_DEVICES, nfound);
    free(cand);
    return s;
}

/* One attempt: lock + verify + dismount EVERY matching device. DM_OK with *out = NULL if none exists;
   DM_E_LOCKED (retry) if one cannot be locked or vanished between the scan and the open (re-enumeration in
   progress: the next attempt rescans instead of assuming it is safe). */
static dm_status lock_old_volume_once(uint32_t disk_number, uint64_t start, uint64_t length, dmw_volume **out)
{
    uint32_t found[DMW_MAX_OLD_DEVICES], nfound = 0;
    dm_status s = find_devices(disk_number, start, length, found, &nfound);
    if (s == DM_E_NOT_FOUND) return DM_OK;   /* no volume device at the old offset: nothing can mount it */
    if (s != DM_OK) return s == DM_E_NOMEM ? DM_E_LOCKED : s;

    dmw_volume *chain = NULL;
    for (uint32_t i = 0; i < nfound; i++) {
        wchar_t path[64];
        volume_device_path(found[i], path, 64);
        dmw_volume *v = NULL;
        s = dmw_volume_lock(path, &v);       /* a read/write open: Windows mounts it first (RAW once invalidated) */
        if (s == DM_OK && (dmw_volume_verify(v, disk_number, start, length) != DM_OK || dmw_volume_dismount(v) != DM_OK)) {
            dmw_volume_close(v);
            s = DM_E_LOCKED;
        }
        if (s != DM_OK) {
            dmw_volume_close(chain);
            return DM_E_LOCKED;
        }
        v->next = chain;
        chain = v;
    }
    *out = chain;
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

DMW_API dm_status DMW_CALL dmw_lock_old_offset(const wchar_t *disk_path, uint64_t start, uint64_t length, dmw_volume **out)
{
    if (!out) return DM_E_INVALID_ARG;
    *out = NULL;
    if (!disk_path || !disk_path[0]) return DM_E_INVALID_ARG;
    uint32_t disk_number = 0;
    dm_status s = dmw_disk_number(disk_path, &disk_number);
    if (s != DM_OK) return s;
    return dmw_lock_old_volume(disk_number, start, length, out);
}

/* mountmgr.h is not in every SDK: the two definitions needed. */
#define DMW_IOCTL_MOUNTMGR_VOLUME_ARRIVAL_NOTIFICATION CTL_CODE(0x0000006D, 11, METHOD_BUFFERED, FILE_READ_ACCESS)

DMW_API dm_status DMW_CALL dmw_volume_arrival_nudge(const wchar_t *disk_path, uint64_t start, uint64_t length)
{
    if (!disk_path || !disk_path[0]) return DM_E_INVALID_ARG;
    uint32_t disk_number = 0, found[DMW_MAX_OLD_DEVICES], nfound = 0;
    dm_status s = dmw_disk_number(disk_path, &disk_number);
    if (s == DM_OK) s = find_devices(disk_number, start, length, found, &nfound);
    if (s != DM_OK) return s;
    HANDLE mm = CreateFileW(L"\\\\.\\MountPointManager", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                            OPEN_EXISTING, 0, NULL);
    if (mm == INVALID_HANDLE_VALUE) return dmw_status_from_win32(GetLastError());
    for (uint32_t i = 0; i < nfound && s == DM_OK; i++) {
        /* MOUNTMGR_TARGET_NAME: USHORT DeviceNameLength (bytes), WCHAR DeviceName[] (not terminated). */
        union { USHORT len; uint8_t raw[2 + 64 * sizeof(wchar_t)]; } t;
        wchar_t name[64];
        int chars = swprintf(name, 64, L"\\Device\\HarddiskVolume%u", found[i]);
        t.len = (USHORT)(chars * sizeof(wchar_t));
        memcpy(t.raw + 2, name, t.len);
        DWORD ret = 0;
        if (!DeviceIoControl(mm, DMW_IOCTL_MOUNTMGR_VOLUME_ARRIVAL_NOTIFICATION, &t, 2 + t.len, NULL, 0, &ret, NULL))
            s = dmw_status_from_win32(GetLastError());
    }
    CloseHandle(mm);
    return s;
}
