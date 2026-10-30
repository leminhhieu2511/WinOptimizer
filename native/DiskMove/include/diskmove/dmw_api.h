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
   If the ACL cannot be set: DM_E_ACCESS and the file is deleted. Journal v2: every plan must carry
   boot_first / boot_backup (see dmw_read_boot_sectors), else DM_E_INVALID_ARG before the file is touched. */
DMW_API dm_status DMW_CALL dmw_journal_create(const wchar_t *path, const dm_move_plan *moves, uint32_t count,
                                              const void *app_blob, uint32_t app_len);
/* Same contract as dm_journal_read. */
DMW_API dm_status DMW_CALL dmw_journal_read(const wchar_t *path, dm_journal_info *info,
                                            void *app_blob, uint32_t app_cap, uint32_t *app_len);
DMW_API dm_status DMW_CALL dmw_journal_set_step(const wchar_t *path, uint32_t step);

/* Runs (or resumes) move move_index on the disk (unbuffered, write-through I/O). progress is the core's
   dm_progress_cb (may be NULL); return non-zero from it to stop between two chunks (DM_E_STOPPED).
   Before any write it reads the disk's layout: it must be GPT with the journal's disk GUID and hold the
   journal's partition (same length) at old_start or old_start + shift; otherwise DM_E_JOURNAL_MISMATCH.
   Then, still before any write, unless held_lock is given: it looks for a volume device whose extent is exactly
   [old_start, old_start + length) on this disk (a hidden, half-moved partition keeps one, and Windows can mount
   it on any re-enumeration). If there is one it is locked + dismounted and held until the call returns; if it
   cannot be locked: DM_E_LOCKED, nothing written. held_lock (may be NULL): a lock the caller took itself on this
   move's volume at old_start (verified, dismounted) and still holds; the DLL then does not look for one. */
DMW_API dm_status DMW_CALL dmw_run_move(const wchar_t *disk_path, const wchar_t *journal_path, uint32_t move_index,
                                        dm_progress_cb progress, void *user, dmw_volume *held_lock);

/* Finds EVERY volume device whose only extent is exactly [start, start + length) on the disk (a hidden, half-moved
   partition keeps one at its old offset), locks + dismounts all of them and returns them as one handle in *out
   (NULL if there is none); close it with dmw_volume_close. Callers hold it across a whole resumed step (copy,
   pauses, NTFS patch, GPT commit) and pass it to dmw_run_move as held_lock. DM_E_LOCKED if one cannot be locked
   (5 tries, 1 s apart; a device that vanishes meanwhile triggers a rescan, never a silent success). */
DMW_API dm_status DMW_CALL dmw_lock_old_offset(const wchar_t *disk_path, uint64_t start, uint64_t length, dmw_volume **out);

/* Writes nothing to the disk: asks the mount manager (IOCTL_MOUNTMGR_VOLUME_ARRIVAL_NOTIFICATION) to process the
   arrival of every volume device whose only extent is exactly [start, start + length) on the disk. For a committed
   volume that exists but never got its \\?\Volume{GUID} link / drive letter. DM_E_NOT_FOUND if there is no such
   device. */
DMW_API dm_status DMW_CALL dmw_volume_arrival_nudge(const wchar_t *disk_path, uint64_t start, uint64_t length);

/* Read-only: the partition's first logical sector (at start) and its last one (start + length - sector_size, the
   NTFS backup boot sector), sector_size bytes each, into first / backup. For the journal v2 plan: call it after the
   volume is locked + dismounted and before any write. DM_E_INVALID_ARG for a sector size other than 512 / 4096 or
   an unaligned range. */
DMW_API dm_status DMW_CALL dmw_read_boot_sectors(const wchar_t *disk_path, uint64_t start, uint64_t length,
                                                 uint32_t sector_size, uint8_t *first, uint8_t *backup);

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
/* Read-only: the GPT attributes of the entry with this PartitionId (GET_DRIVE_LAYOUT_EX, GENERIC_READ). */
DMW_API dm_status DMW_CALL dmw_get_partition_attributes(const wchar_t *disk_path, const uint8_t partition_guid[16],
                                                        uint64_t *attributes);

/* Read-only guard for test tools: *is_system = 1 if disk_number holds any extent of the volume of the
   Windows directory, or has an EFI system partition (GPT type c12a7328-...) / an active or 0xEF partition
   (MBR). Fails closed: on any error *is_system = 1 and the error is returned. The product does NOT use
   this (it targets the system disk by design). */
DMW_API dm_status DMW_CALL dmw_disk_is_system(uint32_t disk_number, int *is_system);

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
