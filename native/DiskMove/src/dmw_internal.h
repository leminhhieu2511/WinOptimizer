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
   (committed), and the destination [old_start + shift, old_start + shift + length) must lie inside the GPT
   usable range and overlap no other partition. DM_E_JOURNAL_MISMATCH otherwise; DM_E_INVALID_ARG for NULL. */
dm_status dmw_layout_check_move(const DRIVE_LAYOUT_INFORMATION_EX *layout, const dm_move_plan *plan);

/* dmw_run_move with the disk identity check (and the old-offset volume lock that follows it) optional
   (check = 0 lets tests move inside a plain image file). */
dm_status dmw_run_move_ex(const wchar_t *disk_path, const wchar_t *journal_path, uint32_t move_index,
                          dm_progress_cb progress, void *user, dmw_volume *held_lock, int check_identity);

/* Disk read-only: GENERIC_READ, share R|W, OPEN_EXISTING, NO_BUFFERING. */
dm_status dmw_disk_open_read(const wchar_t *path, dmw_file *f);

/* ---- old-offset volume of a half-moved partition ---- */

/* \\?\GLOBALROOT\Device\HarddiskVolumeN is scanned for N = 1..DMW_VOLUME_SCAN_MAX (the investigation saw
   HarddiskVolume412 after a day of VHDX attaches, so 256 is too few; a missing N costs one failed open). */
#define DMW_VOLUME_SCAN_MAX 4096u

/* IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS of volume device N into buf (size bytes). Any failure (no such device:
   DM_E_NOT_FOUND) makes the scan skip N. */
typedef dm_status (__cdecl *dmw_extents_query)(void *ctx, uint32_t n, VOLUME_DISK_EXTENTS *buf, uint32_t size);

/* The real query: zero-access open of \\?\GLOBALROOT\Device\HarddiskVolumeN (a direct device open never mounts
   a file system), then the IOCTL. ctx is unused. */
dm_status __cdecl dmw_query_volume_device(void *ctx, uint32_t n, VOLUME_DISK_EXTENTS *buf, uint32_t size);

/* Pure part of the lookup: EVERY candidate N whose volume is exactly one extent [start, start + length) on
   disk_number (dmw_extents_match), in candidate order, into found[0..cap). DM_OK with *nfound >= 1;
   DM_E_NOT_FOUND if none; DM_E_NOMEM if more than cap match. */
dm_status dmw_find_volume_devices(dmw_extents_query query, void *ctx, const uint32_t *cand, uint32_t ncand,
                                  uint32_t disk_number, uint64_t start, uint64_t length,
                                  uint32_t *found, uint32_t cap, uint32_t *nfound);

/* Candidate volume numbers: every \Device\HarddiskVolumeN in the object directory (NtQueryDirectoryObject, any N),
   *from_directory = 1; if the directory cannot be read, 1..DMW_VOLUME_SCAN_MAX, *from_directory = 0.
   Free *out with dmw_free. */
dm_status dmw_volume_numbers(uint32_t **out, uint32_t *count, int *from_directory);

/* "HarddiskVolume<digits>" (len wide chars, not terminated) -> N; 0 for anything else or an overflow. */
int dmw_parse_volume_name(const wchar_t *name, size_t len, uint32_t *n);

/* Pure (review M4): may dmw_run_move go on with the partition where the layout has it? A move that can still
   write (not an earlier one, not complete + boot_restored) needs it at old_start; info NULL: only at old_start. */
int dmw_move_position_ok(const dm_journal_info *info, uint32_t move_index, int at_old);

/* Finds the volume device at exactly [start, start + length) on disk_number; if there is one: open it (Windows
   mounts RAW on an invalidated boot sector), FSCTL_LOCK_VOLUME, re-verify the extent, FSCTL_DISMOUNT_VOLUME, and
   return it in *out (the caller closes it). None: DM_OK with *out = NULL. Exists but cannot be locked / verified /
   dismounted: DM_E_LOCKED (nothing held) after DMW_OLD_LOCK_ATTEMPTS tries 1 s apart. */
#define DMW_OLD_LOCK_ATTEMPTS 5
#define DMW_MAX_OLD_DEVICES   8

/* Pure: does dmw_run_move need the old-offset lock? Only when dm_run_move can write: at_old (the partition is
   still at old_start in the layout) and the move is neither an earlier one nor complete with boot_restored.
   info NULL (unknown): yes. Opening the old device read/write forces Windows to mount it (RAW), so it is not
   done when nothing will be written. */
int dmw_move_needs_old_lock(const dm_journal_info *info, uint32_t move_index, int at_old);
dm_status dmw_lock_old_volume(uint32_t disk_number, uint64_t start, uint64_t length, dmw_volume **out);

#endif /* DISKMOVE_DMW_INTERNAL_H */
