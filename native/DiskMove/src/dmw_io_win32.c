/* Win32 dm_io: unbuffered write-through disk I/O and write-through journal I/O. */
#include "dmw_internal.h"

dm_status dmw_status_from_win32(DWORD err)
{
    switch (err) {
    case ERROR_ACCESS_DENIED:
        return DM_E_ACCESS;
    case ERROR_FILE_NOT_FOUND:
    case ERROR_PATH_NOT_FOUND:
        return DM_E_NOT_FOUND;
    case ERROR_NOT_ENOUGH_MEMORY:
    case ERROR_OUTOFMEMORY:
        return DM_E_NOMEM;
    default:
        return DM_E_IO;
    }
}

static dm_status open_handle(const wchar_t *path, DWORD access, DWORD share, DWORD creation, DWORD flags,
                             int zero_fill_eof, dmw_file *f)
{
    f->h = INVALID_HANDLE_VALUE;
    f->zero_fill_eof = zero_fill_eof;
    if (!path || !path[0]) return DM_E_INVALID_ARG;
    HANDLE h = CreateFileW(path, access, share, NULL, creation, flags, NULL);
    if (h == INVALID_HANDLE_VALUE) return dmw_status_from_win32(GetLastError());
    f->h = h;
    return DM_OK;
}

dm_status dmw_disk_open(const wchar_t *path, dmw_file *f)
{
    return open_handle(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, OPEN_EXISTING,
                       FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH, 0, f);
}

dm_status dmw_journal_open(const wchar_t *path, int write, dmw_file *f)
{
    DWORD access = write ? (GENERIC_READ | GENERIC_WRITE) : GENERIC_READ;
    DWORD share = write ? FILE_SHARE_READ : (FILE_SHARE_READ | FILE_SHARE_WRITE);
    return open_handle(path, access, share, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, 1, f);
}

void dmw_file_close(dmw_file *f)
{
    if (f && f->h != INVALID_HANDLE_VALUE) {
        CloseHandle(f->h);
        f->h = INVALID_HANDLE_VALUE;
    }
}

static OVERLAPPED at(uint64_t off)
{
    OVERLAPPED ov;
    ZeroMemory(&ov, sizeof ov);
    ov.Offset = (DWORD)off;
    ov.OffsetHigh = (DWORD)(off >> 32);
    return ov;
}

/* Synchronous handles: ReadFile/WriteFile honour the OVERLAPPED offset and complete before returning. */
static dm_status file_read(void *ctx, uint64_t off, void *buf, uint32_t len)
{
    dmw_file *f = (dmw_file *)ctx;
    uint8_t *p = (uint8_t *)buf;
    while (len) {
        OVERLAPPED ov = at(off);
        DWORD got = 0;
        if (!ReadFile(f->h, p, len, &got, &ov)) {
            DWORD err = GetLastError();
            if (err == ERROR_HANDLE_EOF && f->zero_fill_eof) got = 0;
            else return dmw_status_from_win32(err);
        }
        if (got == 0) {
            if (!f->zero_fill_eof) return DM_E_IO;   /* a disk never reads short */
            ZeroMemory(p, len);                       /* journal: past EOF reads as zeros */
            return DM_OK;
        }
        p += got; off += got; len -= got;
    }
    return DM_OK;
}

static dm_status file_write(void *ctx, uint64_t off, const void *buf, uint32_t len)
{
    dmw_file *f = (dmw_file *)ctx;
    const uint8_t *p = (const uint8_t *)buf;
    while (len) {
        OVERLAPPED ov = at(off);
        DWORD put = 0;
        if (!WriteFile(f->h, p, len, &put, &ov)) return dmw_status_from_win32(GetLastError());
        if (put == 0) return DM_E_IO;
        p += put; off += put; len -= put;
    }
    return DM_OK;
}

/* A real barrier: the core's crash safety depends on it. */
static dm_status file_flush(void *ctx)
{
    dmw_file *f = (dmw_file *)ctx;
    return FlushFileBuffers(f->h) ? DM_OK : DM_E_IO;
}

dm_io dmw_file_as_dm_io(dmw_file *f)
{
    dm_io io;
    io.ctx = f;
    io.read = file_read;
    io.write = file_write;
    io.flush = file_flush;
    return io;
}
