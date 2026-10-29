/*
 * diskmove-cli: test-only driver for DiskMove.dll (Task 7 runs it against a VHDX). Not shipped.
 *
 *   diskmove-cli move   --disk <path> --volume <path> --disk-guid <g> --part-guid <g> --start <b> --length <b>
 *                       --shift <b> --sector <n> --journal <p> [--no-commit]
 *       1. Locks the volume and verifies (dmw_volume_verify) that it is exactly [start, start+length) on
 *          --disk; refuses otherwise. Nothing is written before this check.
 *       2. Saves the original GPT attributes + mount points to "<journal>.cli-restore", sets
 *          HIDDEN | NO_DRIVE_LETTER and removes the mount points, then dismounts.
 *       3. Creates the journal (one extent: the whole partition), copies, patches the NTFS HiddenSectors and
 *          moves the GPT entry to start + shift, all under the lock.
 *       4. Only after a fully committed move: original attributes back, mount points re-created, sidecar
 *          deleted. --no-commit, a failure after the copy started, or Ctrl+C leave the volume hidden (so
 *          Windows cannot remount it at the old offset) and keep the sidecar; a failure before the first
 *          write undoes the hiding.
 *   diskmove-cli resume --disk <path> --journal <p> --move <i> [--volume <path> --start <old b> --length <b>]
 *       Copy only; the volume stays hidden until commit.
 *   diskmove-cli commit --disk <path> --part-guid <g> --start <new b> [--length <b> [--sector <n>]]
 *                       [--volume <path> --old-start <b>] [--journal <p>]
 *       With --sector the NTFS HiddenSectors are patched first. With --journal the sidecar is restored after
 *       success. --volume (a hidden volume has no letter: use "\\?\Volume{...}") is verified at --old-start
 *       or, after an earlier partial commit, at --start.
 *   diskmove-cli status --journal <p>   (also prints pending restore info)
 *
 * Progress lines: "progress <done>/<total>". Ctrl+C stops between two chunks (exit code 7 = DM_E_STOPPED).
 * Exit code = dm_status of the failing step (0 = success, 1 = bad command line).
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <diskmove/dmw_api.h>
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

/* ---- restore sidecar: "<journal>.cli-restore" ---- */

#define MAX_MOUNTS 8
#define HIDE_ATTRS (DMW_GPT_ATTRIBUTE_HIDDEN | DMW_GPT_ATTRIBUTE_NO_DRIVE_LETTER)

typedef struct restore_info {
    uint64_t attributes;                 /* original GPT attributes */
    wchar_t  volume[MAX_PATH];           /* \\?\Volume{...}\ */
    int      mount_count;
    wchar_t  mounts[MAX_MOUNTS][MAX_PATH];
} restore_info;

static void sidecar_path(const wchar_t *journal, wchar_t *out, size_t cap)
{
    swprintf(out, cap, L"%ls.cli-restore", journal);
}

static int sidecar_write(const wchar_t *path, const restore_info *ri)
{
    FILE *f = _wfopen(path, L"w, ccs=UTF-8");
    if (!f) return 0;
    fwprintf(f, L"attributes 0x%016llX\nvolume %ls\n", (unsigned long long)ri->attributes, ri->volume);
    for (int i = 0; i < ri->mount_count; i++) fwprintf(f, L"mount %ls\n", ri->mounts[i]);
    int ok = fflush(f) == 0 && _commit(_fileno(f)) == 0;
    return fclose(f) == 0 && ok;
}

static int sidecar_read(const wchar_t *path, restore_info *ri)
{
    memset(ri, 0, sizeof *ri);
    FILE *f = _wfopen(path, L"r, ccs=UTF-8");
    if (!f) return 0;
    wchar_t line[MAX_PATH + 32];
    int have_attr = 0;
    while (fgetws(line, MAX_PATH + 32, f)) {
        line[wcscspn(line, L"\r\n")] = 0;
        if (wcsncmp(line, L"attributes ", 11) == 0) {
            ri->attributes = _wcstoui64(line + 11, NULL, 0);
            have_attr = 1;
        } else if (wcsncmp(line, L"volume ", 7) == 0) {
            wcscpy_s(ri->volume, MAX_PATH, line + 7);
        } else if (wcsncmp(line, L"mount ", 6) == 0 && ri->mount_count < MAX_MOUNTS) {
            wcscpy_s(ri->mounts[ri->mount_count++], MAX_PATH, line + 6);
        }
    }
    fclose(f);
    return have_attr && ri->volume[0];
}

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
    for (const wchar_t *m = names; *m && ri->mount_count < MAX_MOUNTS; m += wcslen(m) + 1)
        wcscpy_s(ri->mounts[ri->mount_count++], MAX_PATH, m);
    return 1;
}

static int is_drive_root(const wchar_t *m) { return m[0] && m[1] == L':' && m[2] == L'\\' && !m[3]; }

/* Re-creates the recorded mount points; drops a drive letter Windows auto-assigned meanwhile. */
static int remount(const restore_info *ri)
{
    wchar_t names[4096];
    DWORD len = 0;
    int ok = 1, want_letter = 0;
    for (int i = 0; i < ri->mount_count; i++) want_letter |= is_drive_root(ri->mounts[i]);
    if (GetVolumePathNamesForVolumeNameW(ri->volume, names, 4096, &len)) {
        for (const wchar_t *m = names; *m; m += wcslen(m) + 1) {
            int recorded = 0;
            for (int i = 0; i < ri->mount_count; i++) recorded |= _wcsicmp(m, ri->mounts[i]) == 0;
            if (!recorded && want_letter && is_drive_root(m)) DeleteVolumeMountPointW(m);
        }
    }
    for (int i = 0; i < ri->mount_count; i++) {
        wchar_t cur[MAX_PATH];
        if (GetVolumeNameForVolumeMountPointW(ri->mounts[i], cur, MAX_PATH) && _wcsicmp(cur, ri->volume) == 0)
            continue;   /* already there */
        if (!SetVolumeMountPointW(ri->mounts[i], ri->volume)) {
            fprintf(stderr, "cannot re-create mount point %ls (error %lu)\n", ri->mounts[i], GetLastError());
            ok = 0;
        } else {
            printf("mount point %ls restored\n", ri->mounts[i]);
        }
    }
    return ok;
}

/* Puts the original attributes back (while *v is still locked, so nothing mounts before), releases the
   lock, re-creates the mount points and deletes the sidecar. Returns 0 or a dm_status. */
static int restore_volume(const wchar_t *disk, const uint8_t part_guid[16], const restore_info *ri,
                          const wchar_t *sidecar, dmw_volume **v)
{
    dm_status s = dmw_set_partition_attributes(disk, part_guid, ri->attributes, NULL);
    dmw_volume_close(*v);
    *v = NULL;
    if (s != DM_OK) {
        print_restore_hint(ri, sidecar);
        return report(s, "restore partition attributes");
    }
    printf("partition attributes restored to 0x%016llX\n", (unsigned long long)ri->attributes);
    if (!remount(ri)) {
        print_restore_hint(ri, sidecar);
        return report(DM_E_IO, "restore mount points");
    }
    DeleteFileW(sidecar);
    return 0;
}

/* ---- volume guard ---- */

/* Locks the volume and checks it is exactly the partition [start, start + length) on disk; *v stays NULL
   on failure. Nothing has been written when this returns. */
static int lock_and_verify(const wchar_t *volume_path, const wchar_t *disk, uint64_t start, uint64_t length,
                           uint64_t alt_start, dmw_volume **v)
{
    *v = NULL;
    uint32_t disk_number = 0;
    dm_status s = dmw_disk_number(disk, &disk_number);
    if (s != DM_OK) return report(s, "disk number");
    s = dmw_volume_lock(volume_path, v);
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
        dm_status s = dmw_patch_ntfs_hidden_sectors(disk, new_start, length, sector);
        if (s != DM_OK) return report(s, "patch NTFS hidden sectors");
        printf("hidden sectors patched\n");
    }
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
    sidecar_path(journal, sidecar, MAX_PATH * 2);
    if (GetFileAttributesW(sidecar) != INVALID_FILE_ATTRIBUTES) {
        fprintf(stderr, "%ls exists: a previous move is unfinished (finish it with resume/commit)\n", sidecar);
        return report(DM_E_INVALID_ARG, "move");
    }
    restore_info ri;
    memset(&ri, 0, sizeof ri);
    if (!volume_identity(volume, &ri)) return report(DM_E_INVALID_ARG, "volume identity");

    /* 1. Lock + verify: nothing is written unless --volume really is [start, start+length) on --disk. */
    dmw_volume *v = NULL;
    int rc = lock_and_verify(volume, disk, plan.old_start, plan.length, plan.old_start, &v);
    if (rc) return rc;

    /* 2. Hide it before the first write, so Windows cannot remount it at the old offset if the lock is lost:
          HIDDEN | NO_DRIVE_LETTER, mount points removed; originals saved in the sidecar. */
    dm_status s = dmw_set_partition_attributes(disk, plan.partition_guid, HIDE_ATTRS, &ri.attributes);
    if (s != DM_OK) {
        dmw_volume_close(v);
        return report(s, "hide partition");
    }
    int ok = sidecar_write(sidecar, &ri);
    if ((ri.attributes | HIDE_ATTRS) != HIDE_ATTRS)   /* keep the other original bits */
        s = dmw_set_partition_attributes(disk, plan.partition_guid, ri.attributes | HIDE_ATTRS, NULL);
    if (!ok || s != DM_OK) {
        if (!ok) fprintf(stderr, "cannot write %ls\n", sidecar);
        rc = report(ok ? s : DM_E_IO, "hide partition");
        restore_volume(disk, plan.partition_guid, &ri, sidecar, &v);   /* nothing written yet */
        return rc;
    }
    printf("partition hidden (original attributes 0x%016llX)\n", (unsigned long long)ri.attributes);
    for (int i = 0; i < ri.mount_count; i++) {
        if (!DeleteVolumeMountPointW(ri.mounts[i])) {
            fprintf(stderr, "cannot remove mount point %ls (error %lu)\n", ri.mounts[i], GetLastError());
            rc = report(DM_E_IO, "remove mount points");
            restore_volume(disk, plan.partition_guid, &ri, sidecar, &v);   /* nothing written yet */
            return rc;
        }
        printf("mount point %ls removed\n", ri.mounts[i]);
    }

    rc = dismount(v);
    if (!rc) {
        s = dmw_journal_create(journal, &plan, 1, NULL, 0);
        if (s != DM_OK) rc = report(s, "journal create");
        else printf("journal created\n");
    }
    if (rc) {   /* still nothing written to the disk data: undo the hiding */
        restore_volume(disk, plan.partition_guid, &ri, sidecar, &v);
        return rc;
    }

    /* 3. Copy, then commit; the volume stays hidden until the move is fully committed. */
    s = dmw_run_move(disk, journal, 0, on_progress, NULL);
    if (s != DM_OK) {
        rc = report(s, "move");
    } else {
        printf("move done\n");
        if (flag(L"--no-commit")) {
            printf("not committed (--no-commit)\n");
            dmw_volume_close(v);
            print_restore_hint(&ri, sidecar);
            printf("ok\n");
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
    rc = restore_volume(disk, plan.partition_guid, &ri, sidecar, &v);
    if (!rc) printf("ok\n");
    return rc;
}

/* Optional --volume for resume/commit: needs --length plus the start(s) the volume may sit at. */
static int optional_lock(const wchar_t *disk, uint64_t start, uint64_t alt_start, uint64_t length, dmw_volume **v)
{
    *v = NULL;
    const wchar_t *volume = opt(L"--volume");
    if (!volume) return 0;
    int rc = lock_and_verify(volume, disk, start, length, alt_start, v);
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
    dmw_volume *v = NULL;
    int rc = optional_lock(disk, old_start, old_start, length, &v);
    if (rc) return rc;
    dm_status s = dmw_run_move(disk, journal, (uint32_t)index, on_progress, NULL);
    rc = report(s, "move");
    dmw_volume_close(v);
    if (rc) return rc;
    /* Copied but not committed: the volume must stay hidden until commit restores it. */
    wchar_t sidecar[MAX_PATH * 2];
    restore_info ri;
    sidecar_path(journal, sidecar, MAX_PATH * 2);
    if (sidecar_read(sidecar, &ri)) print_restore_hint(&ri, sidecar);
    printf("move done\nok\n");
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
        sidecar_path(journal, sidecar, MAX_PATH * 2);
        have_restore = sidecar_read(sidecar, &ri);
        if (!have_restore) printf("no restore info in %ls\n", sidecar);
    }

    /* Before the GPT change the volume sits at --old-start; after an earlier partial commit, at --start. */
    dmw_volume *v = NULL;
    int rc = optional_lock(disk, old_start, start, length, &v);
    if (rc) return rc;
    rc = commit(disk, guid, start, length, (uint32_t)sector);
    if (rc) {
        dmw_volume_close(v);
        if (have_restore) print_restore_hint(&ri, sidecar);
        return rc;
    }
    if (have_restore) rc = restore_volume(disk, guid, &ri, sidecar, &v);
    dmw_volume_close(v);
    if (!rc) printf("ok\n");
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
    printf("move_count %u\nstep %u\nmove_index %u\nchunks_done %llu\nchunk_count %llu %llu\nseq %llu\napp_len %u\n",
           info.move_count, info.step, info.move_index, (unsigned long long)info.chunks_done,
           (unsigned long long)info.chunk_count[0], (unsigned long long)info.chunk_count[1],
           (unsigned long long)info.seq, app_len);
    wchar_t sidecar[MAX_PATH * 2];
    restore_info ri;
    sidecar_path(journal, sidecar, MAX_PATH * 2);
    if (sidecar_read(sidecar, &ri)) print_restore_hint(&ri, sidecar);
    return 0;
}

int wmain(int argc, wchar_t **argv)
{
    g_argc = argc;
    g_argv = argv;
    if (argc < 2) return usage();
    SetConsoleCtrlHandler(on_ctrl, TRUE);
    if (wcscmp(argv[1], L"move") == 0) return cmd_move();
    if (wcscmp(argv[1], L"resume") == 0) return cmd_resume();
    if (wcscmp(argv[1], L"commit") == 0) return cmd_commit();
    if (wcscmp(argv[1], L"status") == 0) return cmd_status();
    return usage();
}
