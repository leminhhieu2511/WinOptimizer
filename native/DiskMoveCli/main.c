/*
 * diskmove-cli: test-only driver for DiskMove.dll (Task 7 runs it against a VHDX). Not shipped.
 *
 *   diskmove-cli move   --disk <path> --volume <path> --disk-guid <g> --part-guid <g> --start <b> --length <b>
 *                       --shift <b> --sector <n> --journal <p> [--no-commit]
 *       Locks + dismounts the volume, creates the journal (one extent: the whole partition), copies, then
 *       (unless --no-commit) patches the NTFS HiddenSectors and moves the GPT entry to start + shift, all
 *       while the volume stays locked.
 *   diskmove-cli resume --disk <path> --journal <p> --move <i> [--volume <path>]
 *   diskmove-cli commit --disk <path> --part-guid <g> --start <b> [--length <b> --sector <n>] [--volume <path>]
 *       --start is the NEW start. With --length/--sector the NTFS HiddenSectors are patched first.
 *   diskmove-cli status --journal <p>
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
            "  diskmove-cli resume --disk <path> --journal <p> --move <i> [--volume <path>]\n"
            "  diskmove-cli commit --disk <path> --part-guid <g> --start <b> [--length <b> --sector <n>]"
            " [--volume <path>]\n"
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

/* Locks + dismounts volume_path when given; *v stays NULL otherwise. */
static int lock_volume(const wchar_t *volume_path, dmw_volume **v)
{
    *v = NULL;
    if (!volume_path) return 0;
    dm_status s = dmw_volume_lock(volume_path, v);
    if (s != DM_OK) return report(s, "volume lock");
    printf("volume locked\n");
    s = dmw_volume_dismount(*v);
    if (s != DM_OK) {
        dmw_volume_close(*v);
        *v = NULL;
        return report(s, "volume dismount");
    }
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
        !parse_u64(L"--shift", &plan.shift) || !parse_u64(L"--sector", &sector) || sector > UINT32_MAX)
        return usage();
    plan.sector_size = (uint32_t)sector;
    /* The volume bitmap is unreadable once locked (spike), so the whole partition is one extent. */
    dm_extent whole = { 0, plan.length };
    plan.extent_count = 1;
    plan.extents = &whole;

    dmw_volume *v = NULL;
    int rc = lock_volume(volume, &v);
    if (rc) return rc;
    dm_status s = dmw_journal_create(journal, &plan, 1, NULL, 0);
    if (s != DM_OK) {
        rc = report(s, "journal create");
    } else {
        printf("journal created\n");
        s = dmw_run_move(disk, journal, 0, on_progress, NULL);
        if (s != DM_OK) {
            rc = report(s, "move");
        } else {
            printf("move done\n");
            if (!flag(L"--no-commit"))
                rc = commit(disk, plan.partition_guid, plan.old_start + plan.shift, plan.length, plan.sector_size);
        }
    }
    dmw_volume_close(v);
    if (!rc) printf("ok\n");
    return rc;
}

static int cmd_resume(void)
{
    const wchar_t *disk, *journal;
    uint64_t index = 0;
    if (!need(L"--disk", &disk) || !need(L"--journal", &journal) || !parse_u64(L"--move", &index) ||
        index >= DM_MAX_MOVES)
        return usage();
    dmw_volume *v = NULL;
    int rc = lock_volume(opt(L"--volume"), &v);
    if (rc) return rc;
    dm_status s = dmw_run_move(disk, journal, (uint32_t)index, on_progress, NULL);
    rc = report(s, "move");
    dmw_volume_close(v);
    if (!rc) printf("move done\nok\n");
    return rc;
}

static int cmd_commit(void)
{
    const wchar_t *disk;
    uint8_t guid[16];
    uint64_t start = 0, length = 0, sector = 0;
    if (!need(L"--disk", &disk) || !parse_guid(L"--part-guid", guid) || !parse_u64(L"--start", &start))
        return usage();
    if (opt(L"--length") || opt(L"--sector")) {
        if (!parse_u64(L"--length", &length) || !parse_u64(L"--sector", &sector) || !length || !sector ||
            sector > UINT32_MAX)
            return usage();
    }
    dmw_volume *v = NULL;
    int rc = lock_volume(opt(L"--volume"), &v);
    if (rc) return rc;
    rc = commit(disk, guid, start, length, (uint32_t)sector);
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
