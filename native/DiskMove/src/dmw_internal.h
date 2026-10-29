#ifndef DISKMOVE_DMW_INTERNAL_H
#define DISKMOVE_DMW_INTERNAL_H
/* Internal to DiskMove.dll (and its unit test, which links the objects with DMW_STATIC). */
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <winioctl.h>
#include <diskmove/dmw_api.h>

/* GetLastError() -> dm_status: access denied -> DM_E_ACCESS, file/path not found -> DM_E_NOT_FOUND,
   out of memory -> DM_E_NOMEM, anything else -> DM_E_IO. */
dm_status dmw_status_from_win32(DWORD err);

/* A file/device opened for the core's dm_io. */
typedef struct dmw_file {
    HANDLE h;
    int    zero_fill_eof;   /* journal: reads past EOF return zeros (like an empty journal); disk: DM_E_IO */
} dmw_file;

/* Disk: GENERIC_READ|WRITE, share R|W, OPEN_EXISTING, NO_BUFFERING|WRITE_THROUGH. */
dm_status dmw_disk_open(const wchar_t *path, dmw_file *f);
/* Journal: FILE_FLAG_WRITE_THROUGH, OPEN_EXISTING; write = 0 opens read-only. */
dm_status dmw_journal_open(const wchar_t *path, int write, dmw_file *f);
void      dmw_file_close(dmw_file *f);
dm_io     dmw_file_as_dm_io(dmw_file *f);

/* dmw_journal_create with the ACL step optional (secure = 0 lets non-admin tests reopen the file). */
dm_status dmw_journal_create_ex(const wchar_t *path, const dm_move_plan *moves, uint32_t count,
                                const void *app_blob, uint32_t app_len, int secure);

/* Pure part of dmw_set_partition_start: validates and edits the layout in place. *changed = 0 when the
   partition is already at new_start (nothing to write). sector_size 0 skips the alignment check. */
dm_status dmw_layout_move_partition(DRIVE_LAYOUT_INFORMATION_EX *layout, const uint8_t partition_guid[16],
                                    uint64_t new_start, uint32_t sector_size, int *changed);

/* Pure part of dmw_set_partition_attributes. *old_attributes (may be NULL) gets the previous value;
   *changed = 0 when the attributes already match. */
dm_status dmw_layout_set_attributes(DRIVE_LAYOUT_INFORMATION_EX *layout, const uint8_t partition_guid[16],
                                    uint64_t attributes, uint64_t *old_attributes, int *changed);

/* Pure part of dmw_get_partition_attributes. */
dm_status dmw_layout_get_attributes(const DRIVE_LAYOUT_INFORMATION_EX *layout, const uint8_t partition_guid[16],
                                    uint64_t *attributes);
/* Pure part of dmw_disk_is_system (boot side): GPT ESP, or MBR active / type 0xEF partition. */
int dmw_layout_has_system_partition(const DRIVE_LAYOUT_INFORMATION_EX *layout);

/* Pure part of dmw_volume_verify. */
dm_status dmw_extents_match(const VOLUME_DISK_EXTENTS *ext, uint32_t disk_number, uint64_t start, uint64_t length);

/* Read-only IOCTL_DISK_GET_DRIVE_LAYOUT_EX of disk_path into a malloc'd buffer (caller frees). */
dm_status dmw_query_layout(const wchar_t *disk_path, DRIVE_LAYOUT_INFORMATION_EX **out);

/* Pure part of dmw_run_move's identity check: the layout must be GPT with DiskId == plan->disk_guid and
   hold plan->partition_guid with length plan->length at old_start (not committed yet) or old_start + shift
   (committed). DM_E_JOURNAL_MISMATCH otherwise; DM_E_INVALID_ARG for NULL. */
dm_status dmw_layout_check_move(const DRIVE_LAYOUT_INFORMATION_EX *layout, const dm_move_plan *plan);

/* dmw_run_move with the disk identity check optional (check = 0 lets tests move inside a plain image file). */
dm_status dmw_run_move_ex(const wchar_t *disk_path, const wchar_t *journal_path, uint32_t move_index,
                          dm_progress_cb progress, void *user, int check_identity);

#endif /* DISKMOVE_DMW_INTERNAL_H */
