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
        if (!moves[i].boot_first || !moves[i].boot_backup) return DM_E_INVALID_ARG;   /* journal v2 */
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

/* Before the first write: the disk must be the one the journal was made for (GPT disk GUID) and still hold
   the partition at its old or committed start. The disk number alone (\\.\PhysicalDriveN) can change
   between a crash and the resume. */
static dm_status check_disk_identity(const wchar_t *disk_path, const dm_io *jio, uint32_t move_index,
                                     dm_move_plan *plan, int *at_old)
{
    *at_old = 1;
    dm_extent *chunks = NULL;
    uint32_t n = 0;
    dm_status s = dm_journal_load_move(jio, move_index, plan, &chunks, &n);
    if (s != DM_OK) return s;
    dm_free(chunks);
    DRIVE_LAYOUT_INFORMATION_EX *lay = NULL;
    s = dmw_query_layout(disk_path, &lay);
    if (s == DM_OK) s = dmw_layout_check_move(lay, plan);
    if (s == DM_OK) {
        for (DWORD i = 0; i < lay->PartitionCount; i++) {
            const PARTITION_INFORMATION_EX *e = &lay->PartitionEntry[i];
            if (e->PartitionStyle == PARTITION_STYLE_GPT && memcmp(&e->Gpt.PartitionId, plan->partition_guid, 16) == 0)
                *at_old = (uint64_t)e->StartingOffset.QuadPart == plan->old_start;
        }
    }
    free(lay);
    return s;
}

/* The newest slot (any app blob size). */
static dm_status journal_info(const dm_io *jio, dm_journal_info *info)
{
    uint32_t app_len = 0;
    dm_status s = dm_journal_read(jio, info, NULL, 0, &app_len);
    if (s != DM_E_INVALID_ARG || app_len == 0) return s;
    void *app = malloc(app_len);
    if (!app) return DM_E_NOMEM;
    s = dm_journal_read(jio, info, app, app_len, &app_len);
    free(app);
    return s;
}

/* After the identity check, before any write: a hidden, half-moved partition keeps a volume device at its OLD
   offset, which Windows may mount (and write to) on any re-enumeration. Hold a lock on it for the whole run. */
static dm_status lock_old_offset(const wchar_t *disk_path, const dm_move_plan *plan, dmw_volume **own)
{
    uint32_t disk_number = 0;
    dm_status s = dmw_disk_number(disk_path, &disk_number);
    if (s != DM_OK) return s;
    return dmw_lock_old_volume(disk_number, plan->old_start, plan->length, own);
}

DMW_API dm_status DMW_CALL dmw_run_move(const wchar_t *disk_path, const wchar_t *journal_path, uint32_t move_index,
                                        dm_progress_cb progress, void *user, dmw_volume *held_lock)
{
    return dmw_run_move_ex(disk_path, journal_path, move_index, progress, user, held_lock, 1);
}

dm_status dmw_run_move_ex(const wchar_t *disk_path, const wchar_t *journal_path, uint32_t move_index,
                          dm_progress_cb progress, void *user, dmw_volume *held_lock, int check_identity)
{
    if (!disk_path || !disk_path[0] || !journal_path || !journal_path[0]) return DM_E_INVALID_ARG;
    dmw_file jf, df;
    dmw_volume *own = NULL;
    dm_status s = dmw_journal_open(journal_path, 1, &jf);
    if (s != DM_OK) return s;
    if (check_identity) {
        dm_io cio = dmw_file_as_dm_io(&jf);
        dm_move_plan plan;
        dm_journal_info info;
        int at_old = 1;
        s = check_disk_identity(disk_path, &cio, move_index, &plan, &at_old);
        /* Only when this call can write: opening the old device read/write makes Windows mount it (RAW). */
        if (s == DM_OK && !held_lock &&
            dmw_move_needs_old_lock(journal_info(&cio, &info) == DM_OK ? &info : NULL, move_index, at_old))
            s = lock_old_offset(disk_path, &plan, &own);
        if (s != DM_OK) {
            dmw_file_close(&jf);
            return s;
        }
    }
    s = dmw_disk_open(disk_path, &df);
    if (s != DM_OK) {
        dmw_volume_close(own);
        dmw_file_close(&jf);
        return s;
    }
    dm_io dio = dmw_file_as_dm_io(&df), jio = dmw_file_as_dm_io(&jf);
    s = dm_run_move(&dio, &jio, move_index, progress, user);
    dmw_file_close(&df);
    dmw_file_close(&jf);
    dmw_volume_close(own);   /* releases the old-offset lock only after the last write */
    return s;
}

DMW_API dm_status DMW_CALL dmw_free(void *p)
{
    dm_free(p);
    return DM_OK;
}


int dmw_move_needs_old_lock(const dm_journal_info *info, uint32_t move_index, int at_old)
{
    if (!at_old) return 0;                                   /* committed: no device at the old offset to fear */
    if (!info) return 1;
    if (move_index < info->move_index) return 0;             /* finished earlier: dm_run_move does no I/O */
    if (move_index == info->move_index && move_index < DM_MAX_MOVES &&
        info->chunks_done == info->chunk_count[move_index] && info->boot_restored)
        return 0;                                            /* complete and restored: no I/O */
    return 1;
}
