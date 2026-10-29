/* Journal, mover and memory exports. */
#include "dmw_internal.h"
#include <aclapi.h>
#include <sddl.h>

/* Protected DACL: Administrators + SYSTEM full control, nothing inherited. */
static const wchar_t k_journal_sddl[] = L"D:P(A;;FA;;;BA)(A;;FA;;;SY)";

static dm_status validate_moves(const dm_move_plan *moves, uint32_t count)
{
    if (!moves || count < 1 || count > DM_MAX_MOVES) return DM_E_INVALID_ARG;
    for (uint32_t i = 0; i < count; i++) {
        dm_status s = dm_plan_validate(&moves[i]);
        if (s != DM_OK) return s;
    }
    return DM_OK;
}

dm_status dmw_journal_create_ex(const wchar_t *path, const dm_move_plan *moves, uint32_t count,
                                const void *app_blob, uint32_t app_len, int secure)
{
    if (!path || !path[0]) return DM_E_INVALID_ARG;
    /* Validate before CREATE_ALWAYS so a bad call never truncates an existing journal. */
    dm_status s = validate_moves(moves, count);
    if (s != DM_OK) return s;
    if (app_len && !app_blob) return DM_E_INVALID_ARG;

    PSECURITY_DESCRIPTOR sd = NULL;
    PACL dacl = NULL;
    SECURITY_ATTRIBUTES sa = { sizeof sa, NULL, FALSE };
    if (secure) {
        BOOL present = FALSE, defaulted = FALSE;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(k_journal_sddl, SDDL_REVISION_1, &sd, NULL))
            return DM_E_ACCESS;
        if (!GetSecurityDescriptorDacl(sd, &present, &dacl, &defaulted) || !present || !dacl) {
            LocalFree(sd);
            return DM_E_ACCESS;
        }
        sa.lpSecurityDescriptor = sd;   /* applies when the file is new */
    }

    HANDLE h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE | (secure ? WRITE_DAC : 0), 0,
                           secure ? &sa : NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, NULL);
    if (h == INVALID_HANDLE_VALUE) {
        s = dmw_status_from_win32(GetLastError());
        if (sd) LocalFree(sd);
        return s;
    }
    if (secure) {
        /* CREATE_ALWAYS keeps the old security of an existing file, so set the DACL explicitly. */
        DWORD r = SetSecurityInfo(h, SE_FILE_OBJECT, DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                                  NULL, NULL, dacl, NULL);
        LocalFree(sd);
        if (r != ERROR_SUCCESS) {
            CloseHandle(h);
            DeleteFileW(path);
            return DM_E_ACCESS;
        }
    }

    dmw_file f = { h, 1 };
    dm_io io = dmw_file_as_dm_io(&f);
    s = dm_journal_create(&io, moves, count, app_blob, app_len);
    if (s == DM_OK && !FlushFileBuffers(h)) s = DM_E_IO;
    CloseHandle(h);
    if (s != DM_OK) DeleteFileW(path);   /* no half-written journal; no move has started yet */
    return s;
}

DMW_API dm_status DMW_CALL dmw_journal_create(const wchar_t *path, const dm_move_plan *moves, uint32_t count,
                                              const void *app_blob, uint32_t app_len)
{
    return dmw_journal_create_ex(path, moves, count, app_blob, app_len, 1);
}

DMW_API dm_status DMW_CALL dmw_journal_read(const wchar_t *path, dm_journal_info *info,
                                            void *app_blob, uint32_t app_cap, uint32_t *app_len)
{
    if (!info) return DM_E_INVALID_ARG;
    dmw_file f;
    dm_status s = dmw_journal_open(path, 0, &f);
    if (s != DM_OK) return s;
    dm_io io = dmw_file_as_dm_io(&f);
    s = dm_journal_read(&io, info, app_blob, app_cap, app_len);
    dmw_file_close(&f);
    return s;
}

DMW_API dm_status DMW_CALL dmw_journal_set_step(const wchar_t *path, uint32_t step)
{
    dmw_file f;
    dm_status s = dmw_journal_open(path, 1, &f);
    if (s != DM_OK) return s;
    dm_io io = dmw_file_as_dm_io(&f);
    s = dm_journal_set_step(&io, step);
    dmw_file_close(&f);
    return s;
}

DMW_API dm_status DMW_CALL dmw_run_move(const wchar_t *disk_path, const wchar_t *journal_path, uint32_t move_index,
                                        dm_progress_cb progress, void *user)
{
    if (!disk_path || !disk_path[0] || !journal_path || !journal_path[0]) return DM_E_INVALID_ARG;
    dmw_file jf, df;
    dm_status s = dmw_journal_open(journal_path, 1, &jf);
    if (s != DM_OK) return s;
    s = dmw_disk_open(disk_path, &df);
    if (s != DM_OK) {
        dmw_file_close(&jf);
        return s;
    }
    dm_io dio = dmw_file_as_dm_io(&df), jio = dmw_file_as_dm_io(&jf);
    s = dm_run_move(&dio, &jio, move_index, progress, user);
    dmw_file_close(&df);
    dmw_file_close(&jf);
    return s;
}

DMW_API dm_status DMW_CALL dmw_free(void *p)
{
    dm_free(p);
    return DM_OK;
}
