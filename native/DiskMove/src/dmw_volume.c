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
