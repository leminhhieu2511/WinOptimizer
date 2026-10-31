/*
 * diskmove-cli: test-only driver for DiskMove.dll (Task 7 runs it against a VHDX). Not shipped.
 *
 *   diskmove-cli move   --disk <path> --volume <path> --disk-guid <g> --part-guid <g> --start <b> --length <b>
 *                       --shift <b> --sector <n> --journal <p> [--no-commit]
 *       1. Locks the volume and verifies (dmw_volume_verify) that it is exactly [start, start+length) on
 *          --disk; refuses otherwise. Nothing is written before this check.
 *       2. Reads the original GPT attributes, saves them + partition GUID + mount points to
 *          "<journal>.cli-restore" (flushed), then ONE layout write sets original | HIDDEN | NO_DRIVE_LETTER;
 *          removes the mount points, then dismounts.
 *       3. Reads the partition's original first and last (backup boot) sector, creates the journal v2 with them
 *          (one extent: the whole partition), copies (the core zeroes the old boot sectors before the first
 *          chunk and writes the saved ones at the destination after the last), patches the NTFS HiddenSectors
 *          and moves the GPT entry to start + shift, all under the lock.
 *       4. Only after a fully committed move: original attributes back, mount points re-created, sidecar
 *          deleted. --no-commit, a failure after the copy started, or Ctrl+C leave the volume hidden (so
 *          Windows cannot remount it at the old offset) and keep the sidecar; a failure before the first
 *          write undoes the hiding.
 *   diskmove-cli resume --disk <path> --journal <p> --move <i> [--volume <path> --start <old b> --length <b>]
 *       Copy only; the volume stays hidden until commit. Without --volume, DiskMove.dll itself finds the volume
 *       device still sitting at the OLD offset (a hidden partition keeps one) and locks + dismounts it for the
 *       whole copy, so Windows cannot mount the half-moved view and write to it.
 *   diskmove-cli commit --disk <path> --part-guid <g> --start <new b> [--length <b> [--sector <n>]]
 *                       [--volume <path> --old-start <b>] [--journal <p>]
 *       With --sector the NTFS HiddenSectors are patched first. With --journal the sidecar is restored after
 *       success, and a readable journal must show the move complete with its boot sectors restored. --volume (a hidden volume has no letter: use "\\?\Volume{...}") is verified at --old-start
 *       or, after an earlier partial commit, at --start.
 *   diskmove-cli status --journal <p>   (also prints pending restore info)
 *
 * Every command first refuses a --disk that holds the Windows volume or an EFI system / active partition
 * (dmw_disk_is_system; read-only, fails closed).
 *
 * Step lines: "phase <lock|hide|copy|patch|commit-gpt|restore|unhidden|done>"; progress lines: "progress <done>/<total>"
 * (stdout is unbuffered). Ctrl+C stops between two chunks (exit code 7 = DM_E_STOPPED).
 * Exit code = dm_status of the failing step (0 = success, 1 = bad command line).
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <diskmove/dmw_api.h>
#include "cli_restore.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <wctype.h>
#include <io.h>

static volatile LONG g_stop = 0;

static BOOL WINAPI on_ctrl(DWORD type)
{
    if (type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT) {
        InterlockedExchange(&g_stop, 1);
        return TRUE;
    }
    return FALSE;
}

static int __cdecl on_progress(void *user, uint64_t done, uint64_t total)
{
    (void)user;
    printf("progress %llu/%llu\n", (unsigned long long)done, (unsigned long long)total);
    fflush(stdout);
    return g_stop != 0;
}

static const char *status_name(dm_status s)
{
    static const char *names[] = { "DM_OK", "DM_E_INVALID_ARG", "DM_E_INVALID_PLAN", "DM_E_IO", "DM_E_VERIFY",
                                   "DM_E_JOURNAL_CORRUPT", "DM_E_JOURNAL_MISMATCH", "DM_E_STOPPED", "DM_E_NOMEM",
                                   "DM_E_NOT_FOUND", "DM_E_ACCESS", "DM_E_LOCKED" };
    return (unsigned)s < sizeof names / sizeof names[0] ? names[s] : "DM_E_?";
}

/* Prints the failure and returns s, so callers can `return check(...)`. */
static int report(dm_status s, const char *step)
{
    if (s != DM_OK) fprintf(stderr, "error %s (%d) during %s\n", status_name(s), (int)s, step);
    return (int)s;
}

/* Machine-readable step markers (stdout is unbuffered): lock, hide, copy, patch, commit-gpt, restore, done.
   The VHDX kill tests use them (and the "progress" lines) to pick their kill points. */
static void phase(const char *name)
{
    printf("phase %s\n", name);
}

static void done_ok(void)
{
    phase("done");
    printf("ok\n");
}

static int usage(void)
{
    fprintf(stderr,
            "usage:\n"
            "  diskmove-cli move --disk <path> --volume <path> --disk-guid <g> --part-guid <g> --start <b>\n"
            "                    --length <b> --shift <b> --sector <n> --journal <p> [--no-commit]\n"
            "  diskmove-cli resume --disk <path> --journal <p> --move <i> [--volume <path> --start <old> --length <b>]\n"
            "  diskmove-cli commit --disk <path> --part-guid <g> --start <new> [--length <b> [--sector <n>]]\n"
            "                      [--volume <path> --old-start <b>] [--journal <p>]\n"
            "  diskmove-cli status --journal <p>\n");
    return 1;
}

/* ---- argument parsing ---- */

static int g_argc;
static wchar_t **g_argv;

static const wchar_t *opt(const wchar_t *name)
{
    for (int i = 2; i + 1 < g_argc; i++)
        if (wcscmp(g_argv[i], name) == 0) return g_argv[i + 1];
    return NULL;
}

static int flag(const wchar_t *name)
{
    for (int i = 2; i < g_argc; i++)
        if (wcscmp(g_argv[i], name) == 0) return 1;
    return 0;
}

static int need(const wchar_t *name, const wchar_t **out)
{
    *out = opt(name);
    if (!*out) fprintf(stderr, "missing %ls\n", name);
    return *out != NULL;
}

static int parse_u64(const wchar_t *name, uint64_t *out)
{
    const wchar_t *v;
    if (!need(name, &v)) return 0;
    wchar_t *end = NULL;
    *out = _wcstoui64(v, &end, 0);
    if (!end || *end || end == v) {
        fprintf(stderr, "bad number for %ls: %ls\n", name, v);
        return 0;
    }
    return 1;
}

static int hexval(wchar_t c)
{
    if (c >= L'0' && c <= L'9') return c - L'0';
    if (c >= L'a' && c <= L'f') return c - L'a' + 10;
    if (c >= L'A' && c <= L'F') return c - L'A' + 10;
    return -1;
}

/* "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" (braces optional) -> the 16 bytes of a Windows GUID struct. */
static int parse_guid(const wchar_t *name, uint8_t out[16])
{
    const wchar_t *v;
    if (!need(name, &v)) return 0;
    wchar_t hex[33];
    int n = 0, i = 0;
    const wchar_t *p = v;
    if (*p == L'{') p++;
    for (; *p && *p != L'}'; p++, i++) {
        if (i == 8 || i == 13 || i == 18 || i == 23) {
            if (*p != L'-') goto bad;
            continue;
        }
        if (hexval(*p) < 0 || n >= 32) goto bad;
        hex[n++] = *p;
    }
    if (n != 32 || i != 36) goto bad;
    uint8_t raw[16];
    for (int k = 0; k < 16; k++) raw[k] = (uint8_t)(hexval(hex[2 * k]) << 4 | hexval(hex[2 * k + 1]));
    GUID g;
    g.Data1 = (DWORD)raw[0] << 24 | (DWORD)raw[1] << 16 | (DWORD)raw[2] << 8 | raw[3];
    g.Data2 = (WORD)(raw[4] << 8 | raw[5]);
    g.Data3 = (WORD)(raw[6] << 8 | raw[7]);
    memcpy(g.Data4, raw + 8, 8);
    memcpy(out, &g, 16);
    return 1;
bad:
    fprintf(stderr, "bad GUID for %ls: %ls\n", name, v);
    return 0;
}

/* ---- restore sidecar (cli_restore.c) ---- */

static void print_restore_hint(const restore_info *ri, const wchar_t *sidecar)
{
    printf("volume left hidden: original attributes 0x%016llX, volume %ls\n", (unsigned long long)ri->attributes,
           ri->volume);
    for (int i = 0; i < ri->mount_count; i++) printf("  original mount point %ls\n", ri->mounts[i]);
    printf("restore info kept in %ls (commit --journal restores it)\n", sidecar);
}

/* --volume ("\\.\X:", "X:", "X:\" or "\\?\Volume{...}[\]") -> "\\?\Volume{...}\" + its mount points. */
static int volume_identity(const wchar_t *volume_arg, restore_info *ri)
{
    wchar_t root[MAX_PATH];
    if (wcsncmp(volume_arg, L"\\\\?\\Volume{", 11) == 0) {
        swprintf(ri->volume, MAX_PATH, L"%ls", volume_arg);
    } else {
        const wchar_t *p = wcsncmp(volume_arg, L"\\\\.\\", 4) == 0 ? volume_arg + 4 : volume_arg;
        if (!iswalpha(p[0]) || p[1] != L':') {
            fprintf(stderr, "unsupported --volume form: %ls\n", volume_arg);
            return 0;
        }
        swprintf(root, MAX_PATH, L"%lc:\\", p[0]);
        if (!GetVolumeNameForVolumeMountPointW(root, ri->volume, MAX_PATH)) {
            fprintf(stderr, "cannot resolve the volume name of %ls (error %lu)\n", root, GetLastError());
            return 0;
        }
    }
    size_t n = wcslen(ri->volume);
    if (n + 1 < MAX_PATH && ri->volume[n - 1] != L'\\') wcscat_s(ri->volume, MAX_PATH, L"\\");

    wchar_t names[4096];
    DWORD len = 0;
    ri->mount_count = 0;
    if (!GetVolumePathNamesForVolumeNameW(ri->volume, names, 4096, &len)) {
        fprintf(stderr, "cannot list mount points of %ls (error %lu)\n", ri->volume, GetLastError());
        return 0;
    }
    for (const wchar_t *m = names; *m && ri->mount_count < CLI_MAX_MOUNTS; m += wcslen(m) + 1)
        wcscpy_s(ri->mounts[ri->mount_count++], MAX_PATH, m);
    return 1;
}

static int is_drive_root(const wchar_t *m) { return m[0] && m[1] == L':' && m[2] == L'\\' && !m[3]; }

/* Drops drive letters of the volume that are not recorded (Windows auto-assigned them), if it had one. */
static void drop_auto_letters(const restore_info *ri)
{
    wchar_t names[4096];
    DWORD len = 0;
    int want_letter = 0;
    for (int i = 0; i < ri->mount_count; i++) want_letter |= is_drive_root(ri->mounts[i]);
    if (!want_letter || !GetVolumePathNamesForVolumeNameW(ri->volume, names, 4096, &len)) return;
    for (const wchar_t *m = names; *m; m += wcslen(m) + 1) {
        int recorded = 0;
        for (int i = 0; i < ri->mount_count; i++) recorded |= _wcsicmp(m, ri->mounts[i]) == 0;
        if (!recorded && is_drive_root(m) && DeleteVolumeMountPointW(m)) printf("auto-assigned %ls removed\n", m);
    }
}

/* The mount manager knows the volume: its \\?\Volume{GUID} name resolves to paths, or FindFirstVolume lists it. */
static int volume_registered(const wchar_t *volume)
{
    wchar_t names[1024];
    DWORD len = 0;
    if (GetVolumePathNamesForVolumeNameW(volume, names, 1024, &len)) return 1;
    wchar_t name[MAX_PATH];
    HANDLE h = FindFirstVolumeW(name, MAX_PATH);
    if (h == INVALID_HANDLE_VALUE) return 1;   /* cannot prove it is unregistered */
    int listed = 0;
    do listed |= _wcsicmp(name, volume) == 0; while (!listed && FindNextVolumeW(h, name, MAX_PATH));
    FindVolumeClose(h);
    return listed;
}

/* Where the volume is now, for the arrival nudge (review I1). length 0: no nudge. */
typedef struct remount_ctx { const wchar_t *disk; uint64_t start, length; } remount_ctx;

/* Re-creates the recorded mount points; drops a drive letter Windows auto-assigned meanwhile. A volume that never
   gets registered by the mount manager (seen after a kill right after the unhide: error 87) gets, after 5 s, an
   arrival nudge that writes nothing to the disk (on the VHDX it brought the letter back every time it was needed).
   Returns 1 if every mount point is back; a missing one is a warning. */
static int remount(const restore_info *ri, const remount_ctx *rc)
{
    int ok = 1, nudged = 0;
    drop_auto_letters(ri);
    for (int i = 0; i < ri->mount_count; i++) {
        /* The volume re-arrives asynchronously after UPDATE_PROPERTIES, and Windows may give it its old letter
           back by itself meanwhile (seen on the VHDX: SetVolumeMountPoint then fails with error 145), so every
           attempt first checks whether the mount point already leads to the volume. Up to ~20 s. */
        int set = 0, already = 0;
        DWORD err = 0;
        for (int attempt = 0; attempt < 40 && !set; attempt++) {
            wchar_t cur[MAX_PATH];
            int resolves = GetVolumeNameForVolumeMountPointW(ri->mounts[i], cur, MAX_PATH);
            if (cli_mount_point_is(resolves ? cur : NULL, ri->volume)) {
                set = already = 1;
            } else if (SetVolumeMountPointW(ri->mounts[i], ri->volume)) {
                set = 1;
            } else {
                err = GetLastError();
                if (rc && rc->length && attempt >= 10 && !nudged && !volume_registered(ri->volume)) {
                    nudged = 1;
                    dm_status ns = dmw_volume_arrival_nudge(rc->disk, rc->start, rc->length);
                    printf("volume not registered after 5 s: mount manager arrival nudge %s (%d)\n",
                           ns == DM_OK ? "sent" : "failed", (int)ns);
                }
                Sleep(500);
            }
        }
        if (!set) {
            printf("warning: mount point %ls not re-created (error %lu); the data is intact, give the volume a "
                   "letter in Disk Management\n", ri->mounts[i], err);
            ok = 0;
        } else {
            printf(already ? "mount point %ls already back%s\n" : "mount point %ls restored%s\n", ri->mounts[i],
                   nudged ? " (after the arrival nudge)" : "");
        }
    }
    drop_auto_letters(ri);   /* a letter auto-assigned while we waited */
    return ok;
}

/* Puts the original attributes back (while *v is still locked, so nothing mounts before), releases the
   lock, re-creates the mount points and deletes the sidecar. A mount point that cannot be re-created is only a
   warning (review I1): the partition itself is restored. Returns 0 or a dm_status. */
static int restore_volume(const wchar_t *disk, const uint8_t part_guid[16], const restore_info *ri,
                          const wchar_t *sidecar, dmw_volume **v, uint64_t start, uint64_t length)
{
    phase("restore");
    dm_status s = dmw_set_partition_attributes(disk, part_guid, ri->attributes, NULL);
    if (s == DM_OK) phase("unhidden");   /* still locked here (kill point of the VHDX tests) */
    dmw_volume_close(*v);
    *v = NULL;
    if (s != DM_OK) {
        print_restore_hint(ri, sidecar);
        return report(s, "restore partition attributes");
    }
    printf("partition attributes restored to 0x%016llX\n", (unsigned long long)ri->attributes);
    remount_ctx rc = { disk, start, length };
    if (!remount(ri, &rc)) printf("warning: not every mount point is back (see above)\n");
    DeleteFileW(sidecar);
    return 0;
}

/* ---- guards ---- */

/* Every command calls this first: --disk must be a whole disk that holds neither the Windows volume nor an
   EFI system / active partition. Read-only; refuses (fails closed) on any error. */
static int guard_disk(const wchar_t *disk, uint32_t *disk_number)
{
    dm_status s = dmw_disk_number(disk, disk_number);
    if (s != DM_OK) return report(s, "disk number");
    int is_system = 1;
    s = dmw_disk_is_system(*disk_number, &is_system);
    if (s != DM_OK) {
        fprintf(stderr, "cannot tell whether disk %u is the system disk: refusing\n", *disk_number);
        return report(s, "system disk check");
    }
    if (is_system) {
        fprintf(stderr, "disk %u holds the Windows volume or a boot/EFI system partition: refusing\n", *disk_number);
        return report(DM_E_INVALID_ARG, "system disk check");
    }
    printf("disk %u is not the system disk\n", *disk_number);
    return 0;
}

/* Locks the volume and checks it is exactly the partition [start, start + length) on disk_number; *v stays
   NULL on failure. Nothing has been written when this returns. */
static int lock_and_verify(const wchar_t *volume_path, uint32_t disk_number, uint64_t start, uint64_t length,
                           uint64_t alt_start, dmw_volume **v)
{
    *v = NULL;
    dm_status s = dmw_volume_lock(volume_path, v);
    if (s != DM_OK) return report(s, "volume lock");
    printf("volume locked\n");
    s = dmw_volume_verify(*v, disk_number, start, length);
    if (s != DM_OK && alt_start != start) s = dmw_volume_verify(*v, disk_number, alt_start, length);
    if (s != DM_OK) {
        fprintf(stderr, "volume %ls is not the partition at %llu (+%llu bytes) on disk %u: refusing\n", volume_path,
                (unsigned long long)start, (unsigned long long)length, disk_number);
        dmw_volume_close(*v);
        *v = NULL;
        return report(s, "volume verify");
    }
    printf("volume verified on disk %u\n", disk_number);
    return 0;
}

static int dismount(dmw_volume *v)
{
    dm_status s = dmw_volume_dismount(v);
    if (s != DM_OK) return report(s, "volume dismount");
    printf("volume dismounted\n");
    return 0;
}

static int commit(const wchar_t *disk, const uint8_t part_guid[16], uint64_t new_start, uint64_t length,
                  uint32_t sector)
{
    if (length && sector) {
        phase("patch");
        dm_status s = dmw_patch_ntfs_hidden_sectors(disk, new_start, length, sector);
        if (s != DM_OK) return report(s, "patch NTFS hidden sectors");
        printf("hidden sectors patched\n");
    }
    phase("commit-gpt");
    dm_status s = dmw_set_partition_start(disk, part_guid, new_start);
    if (s != DM_OK) return report(s, "set partition start");
    printf("partition start set to %llu\n", (unsigned long long)new_start);
    return 0;
}

/* ---- commands ---- */

static int cmd_move(void)
{
    const wchar_t *disk, *volume, *journal;
    dm_move_plan plan;
    uint64_t sector = 0;
    memset(&plan, 0, sizeof plan);
    if (!need(L"--disk", &disk) || !need(L"--volume", &volume) || !need(L"--journal", &journal) ||
        !parse_guid(L"--disk-guid", plan.disk_guid) || !parse_guid(L"--part-guid", plan.partition_guid) ||
        !parse_u64(L"--start", &plan.old_start) || !parse_u64(L"--length", &plan.length) ||
        !parse_u64(L"--shift", &plan.shift) || !parse_u64(L"--sector", &sector) || sector > UINT32_MAX ||
        plan.old_start > UINT64_MAX - plan.shift)
        return usage();
    plan.sector_size = (uint32_t)sector;
    /* The volume bitmap is unreadable once locked (spike), so the whole partition is one extent. */
    dm_extent whole = { 0, plan.length };
    plan.extent_count = 1;
    plan.extents = &whole;
    uint64_t new_start = plan.old_start + plan.shift;

    wchar_t sidecar[MAX_PATH * 2];
    cli_sidecar_path(journal, sidecar, MAX_PATH * 2);
    if (GetFileAttributesW(sidecar) != INVALID_FILE_ATTRIBUTES) {
        fprintf(stderr, "%ls exists: a previous move is unfinished (finish it with resume/commit)\n", sidecar);
        return report(DM_E_INVALID_ARG, "move");
    }
    uint32_t disk_number = 0;
    int rc = guard_disk(disk, &disk_number);
    if (rc) return rc;
    restore_info ri;
    memset(&ri, 0, sizeof ri);
    memcpy(ri.partition_guid, plan.partition_guid, 16);
    if (!volume_identity(volume, &ri)) return report(DM_E_INVALID_ARG, "volume identity");

    /* 1. Lock + verify: nothing is written unless --volume really is [start, start+length) on --disk. */
    dmw_volume *v = NULL;
    phase("lock");
    rc = lock_and_verify(volume, disk_number, plan.old_start, plan.length, plan.old_start, &v);
    if (rc) return rc;

    /* 2. Hide it before the first write, so Windows cannot remount it at the old offset if the lock is lost.
          Read the original attributes, persist them (sidecar, flushed), then ONE layout write sets
          original | HIDDEN | NO_DRIVE_LETTER, so a crash at any point never loses an original bit. */
    phase("hide");
    dm_status s = dmw_get_partition_attributes(disk, plan.partition_guid, &ri.attributes);
    if (s != DM_OK) {
        dmw_volume_close(v);
        return report(s, "read partition attributes");
    }
    if (!cli_sidecar_write(sidecar, &ri)) {
        fprintf(stderr, "cannot write %ls\n", sidecar);
        dmw_volume_close(v);
        DeleteFileW(sidecar);
        return report(DM_E_IO, "write restore sidecar");
    }
    s = dmw_set_partition_attributes(disk, plan.partition_guid, cli_hidden_attributes(ri.attributes), NULL);
    if (s != DM_OK) {
        rc = report(s, "hide partition");
        restore_volume(disk, plan.partition_guid, &ri, sidecar, &v, plan.old_start, plan.length);   /* nothing written yet */
        return rc;
    }
    printf("partition hidden (original attributes 0x%016llX)\n", (unsigned long long)ri.attributes);
    /* The lock handle must still be the volume at the old offset after the layout change. */
    s = dmw_volume_verify(v, disk_number, plan.old_start, plan.length);
    if (s != DM_OK) {
        rc = report(s, "volume verify after hide");
        restore_volume(disk, plan.partition_guid, &ri, sidecar, &v, plan.old_start, plan.length);   /* nothing written yet */
        return rc;
    }
    printf("lock handle still valid after hide\n");
    /* Windows drops the drive letter of a volume that becomes HIDDEN by itself (seen on a VHDX), so a mount
       point that already leads nowhere (or elsewhere) counts as removed. */
    for (int i = 0; i < ri.mount_count; i++) {
        BOOL deleted = DeleteVolumeMountPointW(ri.mounts[i]);
        DWORD err = deleted ? 0 : GetLastError();
        wchar_t now[MAX_PATH];
        int resolves = GetVolumeNameForVolumeMountPointW(ri.mounts[i], now, MAX_PATH);
        if (!cli_mount_point_removed(deleted, resolves ? now : NULL, ri.volume)) {
            fprintf(stderr, "cannot remove mount point %ls (error %lu)\n", ri.mounts[i], err);
            rc = report(DM_E_IO, "remove mount points");
            restore_volume(disk, plan.partition_guid, &ri, sidecar, &v, plan.old_start, plan.length);   /* nothing written yet */
            return rc;
        }
        printf(deleted ? "mount point %ls removed\n" : "mount point %ls already removed by Windows\n", ri.mounts[i]);
    }

    rc = dismount(v);
    /* Journal v2: the original boot sectors, read after the lock + dismount and before any write. */
    static uint8_t boot_first[DM_MAX_SECTOR], boot_backup[DM_MAX_SECTOR];
    if (!rc) {
        s = dmw_read_boot_sectors(disk, plan.old_start, plan.length, plan.sector_size, boot_first, boot_backup);
        if (s != DM_OK) rc = report(s, "read boot sectors");
        else printf("boot sectors saved\n");
    }
    plan.boot_first = boot_first;
    plan.boot_backup = boot_backup;
    if (!rc) {
        s = dmw_journal_create(journal, &plan, 1, NULL, 0);
        if (s != DM_OK) rc = report(s, "journal create");
        else printf("journal created\n");
    }
    if (rc) {   /* still nothing written to the disk data: undo the hiding */
        restore_volume(disk, plan.partition_guid, &ri, sidecar, &v, plan.old_start, plan.length);
        return rc;
    }

    /* 3. Copy, then commit; the volume stays hidden until the move is fully committed. */
    phase("copy");
    s = dmw_run_move(disk, journal, 0, on_progress, NULL, v);   /* our lock covers the old offset */
    if (s != DM_OK) {
        rc = report(s, "move");
    } else {
        printf("move done\n");
        if (flag(L"--no-commit")) {
            printf("not committed (--no-commit)\n");
            dmw_volume_close(v);
            print_restore_hint(&ri, sidecar);
            done_ok();
            return 0;
        }
        rc = commit(disk, plan.partition_guid, new_start, plan.length, plan.sector_size);
    }
    if (rc) {
        dmw_volume_close(v);
        print_restore_hint(&ri, sidecar);
        return rc;
    }

    /* 4. Committed: original attributes back, mount points re-created at the new location. */
    rc = restore_volume(disk, plan.partition_guid, &ri, sidecar, &v, new_start, plan.length);
    if (!rc) done_ok();
    return rc;
}

/* Optional --volume for resume/commit: needs --length plus the start(s) the volume may sit at. */
static int optional_lock(uint32_t disk_number, uint64_t start, uint64_t alt_start, uint64_t length, dmw_volume **v)
{
    *v = NULL;
    const wchar_t *volume = opt(L"--volume");
    if (!volume) return 0;
    int rc = lock_and_verify(volume, disk_number, start, length, alt_start, v);
    if (rc) return rc;
    rc = dismount(*v);
    if (rc) {
        dmw_volume_close(*v);
        *v = NULL;
    }
    return rc;
}

static int cmd_resume(void)
{
    const wchar_t *disk, *journal;
    uint64_t index = 0, old_start = 0, length = 0;
    if (!need(L"--disk", &disk) || !need(L"--journal", &journal) || !parse_u64(L"--move", &index) ||
        index >= DM_MAX_MOVES)
        return usage();
    if (opt(L"--volume") && (!parse_u64(L"--start", &old_start) || !parse_u64(L"--length", &length)))
        return usage();
    uint32_t disk_number = 0;
    int rc = guard_disk(disk, &disk_number);
    if (rc) return rc;
    dmw_volume *v = NULL;
    rc = optional_lock(disk_number, old_start, old_start, length, &v);
    if (rc) return rc;
    phase("copy");
    dm_status s = dmw_run_move(disk, journal, (uint32_t)index, on_progress, NULL, v);   /* NULL: the DLL locks the old offset */
    rc = report(s, "move");
    dmw_volume_close(v);
    if (rc) return rc;
    /* Copied but not committed: the volume must stay hidden until commit restores it. */
    wchar_t sidecar[MAX_PATH * 2];
    restore_info ri;
    cli_sidecar_path(journal, sidecar, MAX_PATH * 2);
    if (cli_sidecar_read(sidecar, &ri)) print_restore_hint(&ri, sidecar);
    printf("move done\n");
    done_ok();
    return 0;
}

static int cmd_commit(void)
{
    const wchar_t *disk, *journal = opt(L"--journal");
    uint8_t guid[16];
    uint64_t start = 0, length = 0, sector = 0, old_start = 0;
    if (!need(L"--disk", &disk) || !parse_guid(L"--part-guid", guid) || !parse_u64(L"--start", &start))
        return usage();
    if (opt(L"--sector") && !opt(L"--length")) return usage();
    if (opt(L"--length") && (!parse_u64(L"--length", &length) || !length)) return usage();
    if (opt(L"--sector") && (!parse_u64(L"--sector", &sector) || !sector || sector > UINT32_MAX)) return usage();
    if (opt(L"--volume") && (!length || !parse_u64(L"--old-start", &old_start))) return usage();

    wchar_t sidecar[MAX_PATH * 2];
    restore_info ri;
    int have_restore = 0;
    if (journal) {
        cli_sidecar_path(journal, sidecar, MAX_PATH * 2);
        have_restore = cli_sidecar_read(sidecar, &ri);
        if (!have_restore) printf("no restore info in %ls\n", sidecar);
        if (have_restore && memcmp(ri.partition_guid, guid, 16) != 0) {
            fprintf(stderr, "%ls belongs to another partition than --part-guid: refusing\n", sidecar);
            return report(DM_E_INVALID_ARG, "restore sidecar");
        }
    }

    if (journal && GetFileAttributesW(journal) != INVALID_FILE_ATTRIBUTES) {
        /* Review M5: never patch / commit a move whose copy or boot sector restore is not finished. */
        dm_journal_info info;
        uint32_t app_len = 0;
        dm_status js = dmw_journal_read(journal, &info, NULL, 0, &app_len);
        if (js == DM_OK || (js == DM_E_INVALID_ARG && app_len)) {
            uint32_t last = info.move_count - 1;
            if (info.move_index != last || info.chunks_done != info.chunk_count[last] || !info.boot_restored) {
                fprintf(stderr, "the journal says the move is not finished (move %u, %llu/%llu chunks, boot_restored %u): "
                        "run resume first\n", info.move_index, (unsigned long long)info.chunks_done,
                        (unsigned long long)info.chunk_count[last], info.boot_restored);
                return report(DM_E_JOURNAL_MISMATCH, "commit");
            }
        } else {
            printf("journal unreadable (%d): no copy was started, committing as asked\n", (int)js);
        }
    }

    uint32_t disk_number = 0;
    int rc = guard_disk(disk, &disk_number);
    if (rc) return rc;
    /* Before the GPT change the volume sits at --old-start; after an earlier partial commit, at --start. */
    dmw_volume *v = NULL;
    rc = optional_lock(disk_number, old_start, start, length, &v);
    if (rc) return rc;
    rc = commit(disk, guid, start, length, (uint32_t)sector);
    if (rc) {
        dmw_volume_close(v);
        if (have_restore) print_restore_hint(&ri, sidecar);
        return rc;
    }
    if (have_restore) rc = restore_volume(disk, guid, &ri, sidecar, &v, start, length);
    dmw_volume_close(v);
    if (!rc) done_ok();
    return rc;
}

static int cmd_status(void)
{
    const wchar_t *journal;
    if (!need(L"--journal", &journal)) return usage();
    dm_journal_info info;
    uint32_t app_len = 0;
    dm_status s = dmw_journal_read(journal, &info, NULL, 0, &app_len);
    if (s == DM_E_INVALID_ARG && app_len) {   /* has an app blob: read it into a big enough buffer */
        void *app = malloc(app_len);
        if (!app) return report(DM_E_NOMEM, "journal read");
        s = dmw_journal_read(journal, &info, app, app_len, &app_len);
        free(app);
    }
    if (s != DM_OK) return report(s, "journal read");
    printf("move_count %u\nstep %u\nmove_index %u\nchunks_done %llu\nchunk_count %llu %llu\nseq %llu\napp_len %u\n"
           "boot_restored %u\n",
           info.move_count, info.step, info.move_index, (unsigned long long)info.chunks_done,
           (unsigned long long)info.chunk_count[0], (unsigned long long)info.chunk_count[1],
           (unsigned long long)info.seq, app_len, info.boot_restored);
    wchar_t sidecar[MAX_PATH * 2];
    restore_info ri;
    cli_sidecar_path(journal, sidecar, MAX_PATH * 2);
    if (cli_sidecar_read(sidecar, &ri)) print_restore_hint(&ri, sidecar);
    return 0;
}

int wmain(int argc, wchar_t **argv)
{
    g_argc = argc;
    g_argv = argv;
    if (argc < 2) return usage();
    /* Unbuffered, so a killed run (Task 7 kill test) still shows how far it got. */
    setvbuf(stdout, NULL, _IONBF, 0);
    SetConsoleCtrlHandler(on_ctrl, TRUE);
    if (wcscmp(argv[1], L"move") == 0) return cmd_move();
    if (wcscmp(argv[1], L"resume") == 0) return cmd_resume();
    if (wcscmp(argv[1], L"commit") == 0) return cmd_commit();
    if (wcscmp(argv[1], L"status") == 0) return cmd_status();
    return usage();
}
