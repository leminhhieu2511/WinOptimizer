#ifndef DISKMOVE_DMW_API_H
#define DISKMOVE_DMW_API_H
/*
 * DiskMove.dll: Win32 wrapper around DiskMoveCore, P/Invoked by WinOptimizer (C#).
 *
 * - Every export is __cdecl, returns dm_status (or void) and takes wide (UTF-16) paths.
 * - GUIDs are the 16 bytes of a Windows GUID struct (= .NET Guid.ToByteArray()).
 * - Disk paths are "\\.\PhysicalDriveN" (or a plain image file, used by the tests); volume paths are
 *   "\\.\X:" or "\\?\Volume{...}" (a trailing backslash is ignored).
 * - Callers need administrator rights for disks, volumes and the journal (its ACL is Administrators +
 *   SYSTEM only).
 */
#include <diskmove/dm_core.h>
#include <wchar.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(DMW_BUILDING)
#define DMW_API __declspec(dllexport)
#elif defined(DMW_STATIC)
#define DMW_API
#else
#define DMW_API __declspec(dllimport)
#endif
#define DMW_CALL __cdecl

typedef struct dmw_volume dmw_volume;   /* opaque: a locked volume */

/* Opens the volume and takes FSCTL_LOCK_VOLUME. DM_E_LOCKED if the volume is in use. */
DMW_API dm_status DMW_CALL dmw_volume_lock(const wchar_t *volume_path, dmw_volume **out);
/* FSCTL_DISMOUNT_VOLUME on a locked volume. */
DMW_API dm_status DMW_CALL dmw_volume_dismount(dmw_volume *v);
/* Closes the handle, which releases the lock. NULL is ignored. */
DMW_API void      DMW_CALL dmw_volume_close(dmw_volume *v);
/* IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS: DM_OK only if the volume is exactly one extent on disk_number
   with StartingOffset == start and ExtentLength == length; otherwise DM_E_INVALID_ARG. Call it right after
   dmw_volume_lock, before any write. */
DMW_API dm_status DMW_CALL dmw_volume_verify(dmw_volume *v, uint32_t disk_number, uint64_t start, uint64_t length);
/* IOCTL_STORAGE_GET_DEVICE_NUMBER of a disk path ("\\.\PhysicalDriveN"); DM_E_INVALID_ARG if not a disk. */
DMW_API dm_status DMW_CALL dmw_disk_number(const wchar_t *disk_path, uint32_t *out);

/* Creates (CREATE_ALWAYS: truncates any old file) the journal with a protected DACL granting only
   Administrators + SYSTEM full control, then writes it through (FILE_FLAG_WRITE_THROUGH + flush).
   If the ACL cannot be set: DM_E_ACCESS and the file is deleted. */
DMW_API dm_status DMW_CALL dmw_journal_create(const wchar_t *path, const dm_move_plan *moves, uint32_t count,
                                              const void *app_blob, uint32_t app_len);
/* Same contract as dm_journal_read. */
DMW_API dm_status DMW_CALL dmw_journal_read(const wchar_t *path, dm_journal_info *info,
                                            void *app_blob, uint32_t app_cap, uint32_t *app_len);
DMW_API dm_status DMW_CALL dmw_journal_set_step(const wchar_t *path, uint32_t step);

/* Runs (or resumes) move move_index on the disk (unbuffered, write-through I/O). progress is the core's
   dm_progress_cb (may be NULL); return non-zero from it to stop between two chunks (DM_E_STOPPED). */
DMW_API dm_status DMW_CALL dmw_run_move(const wchar_t *disk_path, const wchar_t *journal_path, uint32_t move_index,
                                        dm_progress_cb progress, void *user);

/* Moves the GPT entry with this PartitionId to new_start (length, type, attributes, name, GUID kept),
   then IOCTL_DISK_UPDATE_PROPERTIES. Idempotent: DM_OK without writing if it is already at new_start.
   DM_E_INVALID_ARG if the disk is not GPT, new_start is not sector aligned, or the new range overlaps
   another partition or leaves the usable LBA range; DM_E_NOT_FOUND if no entry has that GUID. */
DMW_API dm_status DMW_CALL dmw_set_partition_start(const wchar_t *disk_path, const uint8_t partition_guid[16],
                                                   uint64_t new_start);

#define DMW_GPT_ATTRIBUTE_HIDDEN          0x4000000000000000ull   /* GPT_BASIC_DATA_ATTRIBUTE_HIDDEN */
#define DMW_GPT_ATTRIBUTE_NO_DRIVE_LETTER 0x8000000000000000ull   /* GPT_BASIC_DATA_ATTRIBUTE_NO_DRIVE_LETTER */

/* Sets the GPT attributes of the entry with this PartitionId (same GET/SET_DRIVE_LAYOUT_EX +
   IOCTL_DISK_UPDATE_PROPERTIES path as dmw_set_partition_start). Idempotent: no SET if unchanged.
   *old_attributes (may be NULL) receives the attributes found before the call. */
DMW_API dm_status DMW_CALL dmw_set_partition_attributes(const wchar_t *disk_path, const uint8_t partition_guid[16],
                                                        uint64_t attributes, uint64_t *old_attributes);

/* Sets HiddenSectors (u32 at 0x1C) = start / sector_size in the NTFS boot sector at start and the backup
   boot sector at start + length - sector_size. Sectors whose OEM ID is not "NTFS    " are left alone;
   DM_OK without I/O if start / sector_size > UINT32_MAX. */
DMW_API dm_status DMW_CALL dmw_patch_ntfs_hidden_sectors(const wchar_t *disk_path, uint64_t start, uint64_t length,
                                                         uint32_t sector_size);

/* Frees memory returned by this DLL. NULL is ignored. */
DMW_API dm_status DMW_CALL dmw_free(void *p);

#ifdef __cplusplus
}
#endif

#endif /* DISKMOVE_DMW_API_H */
