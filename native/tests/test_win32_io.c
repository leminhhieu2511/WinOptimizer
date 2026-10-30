/*
 * DiskMove.dll Win32 layer, exercised on temp files only (runs without admin; never opens a disk or a
 * volume). Links the DLL's objects with DMW_STATIC.
 */
#include "dm_test.h"
#include "dmw_internal.h"
#include <aclapi.h>
#include <sddl.h>
#include <stdlib.h>
#include <string.h>

#define KIB 1024ull
#define MIB (1024ull * KIB)
#define MAX_EXT 32

static uint64_t rng_next(uint64_t *s)    /* splitmix64 */
{
    uint64_t z = (*s += 0x9E3779B97F4A7C15ull);
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
    return z ^ (z >> 31);
}

static void temp_path(wchar_t *out, const wchar_t *tag)
{
    wchar_t dir[MAX_PATH];
    DWORD n = GetTempPathW(MAX_PATH, dir);
    ASSERT_TRUE(n > 0 && n < MAX_PATH);
    swprintf(out, MAX_PATH, L"%sdmw_%s_%lu_%lu.bin", dir, tag, GetCurrentProcessId(), GetTickCount());
}

static int write_file(const wchar_t *path, const void *data, uint64_t size)
{
    HANDLE h = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return 0;
    const uint8_t *p = (const uint8_t *)data;
    int ok = 1;
    while (ok && size) {
        DWORD chunk = size > (64 * MIB) ? (DWORD)(64 * MIB) : (DWORD)size, done = 0;
        ok = WriteFile(h, p, chunk, &done, NULL) && done == chunk;
        p += chunk; size -= chunk;
    }
    CloseHandle(h);
    return ok;
}

/* Reads the whole file; returns NULL on failure. */
static uint8_t *read_file(const wchar_t *path, uint64_t *size)
{
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return NULL;
    LARGE_INTEGER sz;
    uint8_t *buf = NULL;
    if (GetFileSizeEx(h, &sz) && (buf = (uint8_t *)malloc((size_t)sz.QuadPart + 1)) != NULL) {
        uint64_t left = (uint64_t)sz.QuadPart;
        uint8_t *p = buf;
        while (left) {
            DWORD chunk = left > (64 * MIB) ? (DWORD)(64 * MIB) : (DWORD)left, done = 0;
            if (!ReadFile(h, p, chunk, &done, NULL) || done != chunk) { free(buf); buf = NULL; break; }
            p += chunk; left -= chunk;
        }
        *size = (uint64_t)sz.QuadPart;
    }
    CloseHandle(h);
    return buf;
}

static uint64_t file_size(const wchar_t *path)
{
    WIN32_FILE_ATTRIBUTE_DATA a;
    if (!GetFileAttributesExW(path, GetFileExInfoStandard, &a)) return 0;
    return ((uint64_t)a.nFileSizeHigh << 32) | a.nFileSizeLow;
}

static uint8_t k_boot[2][4096];

static void small_plan(dm_move_plan *p, dm_extent *ext)
{
    memset(p, 0, sizeof *p);
    p->boot_first = k_boot[0];
    p->boot_backup = k_boot[1];
    memset(p->disk_guid, 0x11, 16);
    memset(p->partition_guid, 0x22, 16);
    p->sector_size = 512;
    p->old_start = 1 * MIB;
    p->length = 64 * MIB;
    p->shift = 8 * MIB;
    ext->offset = 0; ext->length = p->length;
    p->extent_count = 1;
    p->extents = ext;
}

/* ---- journal ---- */

static void journal_roundtrip_truncates_old_file(void)
{
    wchar_t path[MAX_PATH];
    temp_path(path, L"jr");
    /* An old, bigger file must be truncated (CREATE_ALWAYS). */
    uint8_t *junk = (uint8_t *)malloc((size_t)(2 * MIB));
    ASSERT_TRUE(junk != NULL);
    memset(junk, 0xA5, (size_t)(2 * MIB));
    ASSERT_TRUE(write_file(path, junk, 2 * MIB));
    free(junk);

    dm_move_plan p; dm_extent e;
    small_plan(&p, &e);
    const char app[] = "app-blob-v1";
    ASSERT_EQ_INT(dmw_journal_create_ex(path, &p, 1, app, (uint32_t)sizeof app, 0), DM_OK);
    ASSERT_TRUE(file_size(path) < 2 * MIB);

    dm_journal_info info; char got[64]; uint32_t alen = 0;
    memset(&info, 0xFF, sizeof info);
    ASSERT_EQ_INT(dmw_journal_read(path, &info, got, sizeof got, &alen), DM_OK);
    ASSERT_EQ_U32(info.move_count, 1);
    ASSERT_EQ_U32(info.step, 0);
    ASSERT_EQ_U32(info.move_index, 0);
    ASSERT_EQ_U64(info.chunks_done, 0);
    ASSERT_TRUE(info.chunk_count[0] > 0);
    ASSERT_EQ_U64(info.chunk_count[1], 0);
    ASSERT_EQ_U32(alen, sizeof app);
    ASSERT_TRUE(memcmp(got, app, sizeof app) == 0);

    /* Too-small app buffer: same contract as the core. */
    ASSERT_EQ_INT(dmw_journal_read(path, &info, got, 2, &alen), DM_E_INVALID_ARG);
    ASSERT_EQ_U32(alen, sizeof app);

    ASSERT_EQ_INT(dmw_journal_set_step(path, 3), DM_OK);
    ASSERT_EQ_INT(dmw_journal_read(path, &info, NULL, 0, &alen), DM_E_INVALID_ARG);   /* app blob present */
    ASSERT_EQ_INT(dmw_journal_read(path, &info, got, sizeof got, &alen), DM_OK);
    ASSERT_EQ_U32(info.step, 3);
    DeleteFileW(path);
}

static int process_is_elevated(void)
{
    HANDLE tok;
    TOKEN_ELEVATION el = { 0 };
    DWORD n = 0;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok)) return 0;
    BOOL ok = GetTokenInformation(tok, TokenElevation, &el, sizeof el, &n);
    CloseHandle(tok);
    return ok && el.TokenIsElevated;
}

static int ace_is(ACCESS_ALLOWED_ACE *ace, WELL_KNOWN_SID_TYPE type)
{
    BYTE sid[SECURITY_MAX_SID_SIZE];
    DWORD n = sizeof sid;
    if (!CreateWellKnownSid(type, NULL, sid, &n)) return 0;
    return ace->Header.AceType == ACCESS_ALLOWED_ACE_TYPE && (ace->Mask & FILE_ALL_ACCESS) == FILE_ALL_ACCESS &&
           EqualSid((PSID)&ace->SidStart, sid);
}

static void journal_acl_admin_system_only(void)
{
    wchar_t path[MAX_PATH];
    temp_path(path, L"jacl");
    dm_move_plan p; dm_extent e;
    small_plan(&p, &e);
    ASSERT_EQ_INT(dmw_journal_create(path, &p, 1, NULL, 0), DM_OK);

    /* The owner keeps READ_CONTROL, so the DACL is readable even without admin. */
    PACL dacl = NULL;
    PSECURITY_DESCRIPTOR sd = NULL;
    DWORD r = GetNamedSecurityInfoW(path, SE_FILE_OBJECT, DACL_SECURITY_INFORMATION, NULL, NULL, &dacl, NULL, &sd);
    ASSERT_EQ_INT(r, ERROR_SUCCESS);
    if (r == ERROR_SUCCESS) {
        SECURITY_DESCRIPTOR_CONTROL ctl = 0; DWORD rev = 0;
        ASSERT_TRUE(GetSecurityDescriptorControl(sd, &ctl, &rev));
        ASSERT_TRUE((ctl & SE_DACL_PROTECTED) != 0);
        ASSERT_TRUE(dacl != NULL);
        if (dacl) {
            ASSERT_EQ_INT(dacl->AceCount, 2);
            int admin = 0, system = 0;
            for (DWORD i = 0; i < dacl->AceCount; i++) {
                ACCESS_ALLOWED_ACE *ace = NULL;
                if (!GetAce(dacl, i, (void **)&ace)) continue;
                admin += ace_is(ace, WinBuiltinAdministratorsSid);
                system += ace_is(ace, WinLocalSystemSid);
            }
            ASSERT_EQ_INT(admin, 1);
            ASSERT_EQ_INT(system, 1);
        }
        LocalFree(sd);
    }

    /* Elevated: the journal reads back. Not elevated: access is denied by that ACL. */
    dm_journal_info info; uint32_t alen = 0;
    dm_status want = process_is_elevated() ? DM_OK : DM_E_ACCESS;
    ASSERT_EQ_INT(dmw_journal_read(path, &info, NULL, 0, &alen), want);
    ASSERT_TRUE(DeleteFileW(path));   /* FILE_DELETE_CHILD on %TEMP% */
}

static void journal_missing_file(void)
{
    wchar_t path[MAX_PATH];
    temp_path(path, L"nojr");
    dm_journal_info info; uint32_t alen = 0;
    ASSERT_EQ_INT(dmw_journal_read(path, &info, NULL, 0, &alen), DM_E_NOT_FOUND);
    ASSERT_EQ_INT(dmw_journal_set_step(path, 1), DM_E_NOT_FOUND);
    ASSERT_EQ_INT(dmw_journal_read(NULL, &info, NULL, 0, &alen), DM_E_INVALID_ARG);
}

/* ---- mover on an image file (same layout as test_mover's move_basic) ---- */

typedef struct prog { uint64_t calls, last_done, last_total; } prog;

static int __cdecl on_progress(void *user, uint64_t done, uint64_t total)
{
    prog *p = (prog *)user;
    p->calls++; p->last_done = done; p->last_total = total;
    return 0;
}

static void run_move_on_image_file(uint32_t sector, uint64_t seed)
{
    dm_move_plan plan;
    dm_extent ext[MAX_EXT];
    uint64_t rs = seed, len = 64 * MIB;
    memset(&plan, 0, sizeof plan);
    memset(plan.disk_guid, 0x11, 16);
    memset(plan.partition_guid, 0x22, 16);
    plan.sector_size = sector;
    plan.old_start = 1 * MIB;
    plan.length = len;
    plan.shift = 8 * MIB;
    for (uint32_t i = 0; i < 20; i++) {
        uint64_t off = rng_next(&rs) % len;
        uint64_t max = len - off < len / 8 ? len - off : len / 8;
        ext[i].offset = off;
        ext[i].length = 1 + rng_next(&rs) % max;
    }
    plan.extent_count = 20;
    plan.extents = ext;

    uint64_t size = plan.old_start + len + plan.shift + 1 * MIB;
    uint8_t *orig = (uint8_t *)malloc((size_t)size);
    ASSERT_TRUE(orig != NULL);
    if (!orig) return;
    for (uint64_t i = 0; i < size; i += 8) { uint64_t v = rng_next(&rs); memcpy(orig + i, &v, 8); }

    wchar_t disk[MAX_PATH], jr[MAX_PATH];
    temp_path(disk, L"img");
    temp_path(jr, L"imgjr");
    ASSERT_TRUE(write_file(disk, orig, size));
    /* v2: the caller reads the original boot sectors (here through the DLL, as the CLI / runner do). */
    static uint8_t first[4096], backup[4096];
    ASSERT_EQ_INT(dmw_read_boot_sectors(disk, plan.old_start, plan.length, sector, first, backup), DM_OK);
    ASSERT_TRUE(memcmp(first, orig + plan.old_start, sector) == 0);
    ASSERT_TRUE(memcmp(backup, orig + plan.old_start + len - sector, sector) == 0);
    plan.boot_first = first;
    plan.boot_backup = backup;
    ASSERT_EQ_INT(dmw_journal_create_ex(jr, &plan, 1, NULL, 0, 0), DM_OK);

    prog pg = { 0, 0, 0 };
    ASSERT_EQ_INT(dmw_run_move_ex(disk, jr, 0, on_progress, &pg, NULL, 0), DM_OK);
    ASSERT_TRUE(pg.calls > 0);
    ASSERT_EQ_U64(pg.last_done, pg.last_total);
    /* A finished move returns DM_OK again without I/O. */
    ASSERT_EQ_INT(dmw_run_move_ex(disk, jr, 0, NULL, NULL, NULL, 0), DM_OK);

    uint64_t got_size = 0;
    uint8_t *img = read_file(disk, &got_size);
    ASSERT_TRUE(img != NULL);
    ASSERT_EQ_U64(got_size, size);
    if (img && got_size == size) {
        uint64_t dst = plan.old_start + plan.shift;
        ASSERT_TRUE(memcmp(img + dst, orig + plan.old_start, (size_t)MIB) == 0);                       /* head */
        ASSERT_TRUE(memcmp(img + dst + len - MIB, orig + plan.old_start + len - MIB, (size_t)MIB) == 0); /* tail */
        for (uint32_t i = 0; i < plan.extent_count; i++)
            if (memcmp(img + dst + ext[i].offset, orig + plan.old_start + ext[i].offset, (size_t)ext[i].length) != 0)
                DM_FAIL("extent %u not moved", i);
        ASSERT_TRUE(memcmp(img, orig, (size_t)plan.old_start) == 0);
        for (uint32_t i = 0; i < sector; i++)
            if (img[plan.old_start + i]) { DM_FAIL("old first sector not invalidated"); break; }
        uint64_t end = dst + len;
        ASSERT_TRUE(memcmp(img + end, orig + end, (size_t)(size - end)) == 0);
    }
    free(img);

    dm_journal_info info; uint32_t alen = 0;
    ASSERT_EQ_INT(dmw_journal_read(jr, &info, NULL, 0, &alen), DM_OK);
    ASSERT_EQ_U64(info.chunks_done, info.chunk_count[0]);
    free(orig);
    DeleteFileW(disk);
    DeleteFileW(jr);
}

static void move_basic_on_file(void) { run_move_on_image_file(512, 1); }
static void move_4kn_on_file(void) { run_move_on_image_file(4096, 4); }

static int __cdecl stop_now(void *user, uint64_t done, uint64_t total)
{
    (void)user; (void)done; (void)total;
    return 1;
}

static void move_stop_and_missing_paths(void)
{
    dm_move_plan p; dm_extent e;
    small_plan(&p, &e);
    uint64_t size = p.old_start + p.length + p.shift + MIB;
    uint8_t *zero = (uint8_t *)calloc(1, (size_t)size);
    ASSERT_TRUE(zero != NULL);
    wchar_t disk[MAX_PATH], jr[MAX_PATH], none[MAX_PATH];
    temp_path(disk, L"stop");
    temp_path(jr, L"stopjr");
    temp_path(none, L"none");
    ASSERT_TRUE(write_file(disk, zero, size));
    free(zero);
    ASSERT_EQ_INT(dmw_journal_create_ex(jr, &p, 1, NULL, 0, 0), DM_OK);

    ASSERT_EQ_INT(dmw_run_move_ex(disk, jr, 0, stop_now, NULL, NULL, 0), DM_E_STOPPED);
    ASSERT_EQ_INT(dmw_run_move(none, jr, 0, NULL, NULL, NULL), DM_E_NOT_FOUND);
    ASSERT_EQ_INT(dmw_run_move(disk, none, 0, NULL, NULL, NULL), DM_E_NOT_FOUND);
    ASSERT_EQ_INT(dmw_run_move(NULL, jr, 0, NULL, NULL, NULL), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_run_move_ex(disk, jr, 0, NULL, NULL, NULL, 0), DM_OK);   /* resumes after the stop */
    DeleteFileW(disk);
    DeleteFileW(jr);
}

/* ---- NTFS hidden sectors ---- */

static void put_boot_sector(uint8_t *img, uint64_t off, uint32_t hidden)
{
    memcpy(img + off + 3, "NTFS    ", 8);
    memcpy(img + off + 0x1C, &hidden, 4);
    img[off + 510] = 0x55; img[off + 511] = 0xAA;
}

static uint32_t get_u32(const uint8_t *p) { uint32_t v; memcpy(&v, p, 4); return v; }

static void patch_case(uint32_t sector, int backup_is_ntfs)
{
    uint64_t start = 1 * MIB, length = 2 * MIB, size = 4 * MIB;
    uint8_t *img = (uint8_t *)malloc((size_t)size);
    ASSERT_TRUE(img != NULL);
    if (!img) return;
    uint64_t rs = 99;
    for (uint64_t i = 0; i < size; i += 8) { uint64_t v = rng_next(&rs); memcpy(img + i, &v, 8); }
    uint64_t bak = start + length - sector;
    put_boot_sector(img, start, 12345);
    if (backup_is_ntfs) put_boot_sector(img, bak, 12345);
    else memcpy(img + bak + 3, "MSDOS5.0", 8);

    wchar_t disk[MAX_PATH];
    temp_path(disk, L"ntfs");
    ASSERT_TRUE(write_file(disk, img, size));
    ASSERT_EQ_INT(dmw_patch_ntfs_hidden_sectors(disk, start, length, sector), DM_OK);

    uint64_t got_size = 0;
    uint8_t *got = read_file(disk, &got_size);
    ASSERT_TRUE(got != NULL && got_size == size);
    if (got && got_size == size) {
        uint32_t want = (uint32_t)(start / sector);
        ASSERT_EQ_U32(get_u32(got + start + 0x1C), want);
        ASSERT_EQ_U32(get_u32(got + bak + 0x1C), backup_is_ntfs ? want : get_u32(img + bak + 0x1C));
        /* Only the two HiddenSectors fields changed. */
        memcpy(img + start + 0x1C, &want, 4);
        if (backup_is_ntfs) memcpy(img + bak + 0x1C, &want, 4);
        ASSERT_TRUE(memcmp(got, img, (size_t)size) == 0);
    }
    free(got);
    free(img);
    DeleteFileW(disk);
}

static void patch_hidden_sectors_512(void) { patch_case(512, 1); }
static void patch_hidden_sectors_4096(void) { patch_case(4096, 1); }
static void patch_skips_non_ntfs_backup(void) { patch_case(512, 0); }

static void patch_rejects_and_skips(void)
{
    wchar_t none[MAX_PATH];
    temp_path(none, L"none");
    /* start / sector > UINT32_MAX: nothing to do, the disk is not even opened. */
    ASSERT_EQ_INT(dmw_patch_ntfs_hidden_sectors(none, (UINT32_MAX + 1ull) * 512, 2 * MIB, 512), DM_OK);
    ASSERT_EQ_INT(dmw_patch_ntfs_hidden_sectors(none, 1 * MIB, 2 * MIB, 512), DM_E_NOT_FOUND);
    ASSERT_EQ_INT(dmw_patch_ntfs_hidden_sectors(none, 1 * MIB, 2 * MIB, 1000), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_patch_ntfs_hidden_sectors(none, 1 * MIB + 100, 2 * MIB, 512), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_patch_ntfs_hidden_sectors(none, 1 * MIB, 0, 512), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_patch_ntfs_hidden_sectors(NULL, 1 * MIB, 2 * MIB, 512), DM_E_INVALID_ARG);
}

/* ---- GPT layout edit (pure part of dmw_set_partition_start) ---- */

typedef struct layout_buf {
    DRIVE_LAYOUT_INFORMATION_EX hdr;
    PARTITION_INFORMATION_EX more[3];   /* hdr.PartitionEntry[0] + 3 = 4 entries */
} layout_buf;

static void make_layout(layout_buf *b)
{
    memset(b, 0, sizeof *b);
    DRIVE_LAYOUT_INFORMATION_EX *l = &b->hdr;
    l->PartitionStyle = PARTITION_STYLE_GPT;
    l->PartitionCount = 3;
    l->Gpt.StartingUsableOffset.QuadPart = 17408;
    l->Gpt.UsableLength.QuadPart = 4096 * MIB - 17408 - 16896;
    uint64_t starts[3] = { 1 * MIB, 1025 * MIB, 2049 * MIB }, lens[3] = { 1024 * MIB, 512 * MIB, 1024 * MIB };
    for (DWORD i = 0; i < 3; i++) {
        PARTITION_INFORMATION_EX *p = &l->PartitionEntry[i];
        p->PartitionStyle = PARTITION_STYLE_GPT;
        p->StartingOffset.QuadPart = (LONGLONG)starts[i];
        p->PartitionLength.QuadPart = (LONGLONG)lens[i];
        p->PartitionNumber = i + 1;
        memset(&p->Gpt.PartitionType, 0x30 + (int)i, sizeof(GUID));
        memset(&p->Gpt.PartitionId, 0x40 + (int)i, sizeof(GUID));
        p->Gpt.Attributes = 0x8000000000000001ull;
        wcscpy_s(p->Gpt.Name, 36, L"part");
    }
}

static void layout_moves_partition(void)
{
    layout_buf b, before;
    make_layout(&b);
    before = b;
    uint8_t id[16]; memset(id, 0x41, 16);   /* middle partition */
    int changed = -1;
    ASSERT_EQ_INT(dmw_layout_move_partition(&b.hdr, id, 1537 * MIB, 512, &changed), DM_OK);
    ASSERT_EQ_INT(changed, 1);
    PARTITION_INFORMATION_EX *p = &b.hdr.PartitionEntry[1];
    ASSERT_EQ_U64(p->StartingOffset.QuadPart, 1537 * MIB);
    ASSERT_EQ_U64(p->PartitionLength.QuadPart, 512 * MIB);
    ASSERT_TRUE(p->RewritePartition);
    ASSERT_TRUE(memcmp(&p->Gpt, &before.hdr.PartitionEntry[1].Gpt, sizeof p->Gpt) == 0);
    ASSERT_TRUE(memcmp(&b.hdr.PartitionEntry[0], &before.hdr.PartitionEntry[0], sizeof *p) == 0);
    ASSERT_TRUE(memcmp(&b.hdr.PartitionEntry[2], &before.hdr.PartitionEntry[2], sizeof *p) == 0);

    /* Already there: idempotent, nothing to write. */
    before = b;
    ASSERT_EQ_INT(dmw_layout_move_partition(&b.hdr, id, 1537 * MIB, 512, &changed), DM_OK);
    ASSERT_EQ_INT(changed, 0);
    ASSERT_TRUE(memcmp(&b, &before, sizeof b) == 0);
}

static void layout_rejects_bad_moves(void)
{
    layout_buf b;
    uint8_t id[16]; memset(id, 0x41, 16);
    int changed;
    make_layout(&b);
    ASSERT_EQ_INT(dmw_layout_move_partition(&b.hdr, id, 1538 * MIB, 512, &changed), DM_E_INVALID_ARG);   /* overlaps #3 */
    ASSERT_EQ_INT(dmw_layout_move_partition(&b.hdr, id, 512 * MIB, 512, &changed), DM_E_INVALID_ARG);    /* overlaps #1 */
    ASSERT_EQ_INT(dmw_layout_move_partition(&b.hdr, id, 1537 * MIB + 100, 512, &changed), DM_E_INVALID_ARG); /* unaligned */
    ASSERT_EQ_INT(dmw_layout_move_partition(&b.hdr, id, 0, 512, &changed), DM_E_INVALID_ARG);            /* below usable */
    memset(id, 0x42, 16);   /* last partition: past the usable end */
    ASSERT_EQ_INT(dmw_layout_move_partition(&b.hdr, id, 3072 * MIB, 512, &changed), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_layout_move_partition(&b.hdr, id, UINT64_MAX - 4095, 512, &changed), DM_E_INVALID_ARG);
    memset(id, 0x77, 16);
    ASSERT_EQ_INT(dmw_layout_move_partition(&b.hdr, id, 1537 * MIB, 512, &changed), DM_E_NOT_FOUND);
    layout_buf before = b;
    b.hdr.PartitionStyle = PARTITION_STYLE_MBR;
    before.hdr.PartitionStyle = PARTITION_STYLE_MBR;
    memset(id, 0x41, 16);
    ASSERT_EQ_INT(dmw_layout_move_partition(&b.hdr, id, 1537 * MIB, 512, &changed), DM_E_INVALID_ARG);
    ASSERT_TRUE(memcmp(&b, &before, sizeof b) == 0);
}

static void layout_sets_attributes(void)
{
    layout_buf b, before;
    make_layout(&b);
    before = b;
    uint8_t id[16]; memset(id, 0x41, 16);
    const uint64_t hidden = DMW_GPT_ATTRIBUTE_HIDDEN | DMW_GPT_ATTRIBUTE_NO_DRIVE_LETTER | 1;
    uint64_t old = 0;
    int changed = -1;
    ASSERT_EQ_INT(dmw_layout_set_attributes(&b.hdr, id, hidden, &old, &changed), DM_OK);
    ASSERT_EQ_INT(changed, 1);
    ASSERT_EQ_U64(old, 0x8000000000000001ull);
    PARTITION_INFORMATION_EX *p = &b.hdr.PartitionEntry[1];
    ASSERT_EQ_U64(p->Gpt.Attributes, hidden);
    ASSERT_TRUE(p->RewritePartition);
    /* Nothing else moves: offset, length, type, id, name, other entries, header. */
    ASSERT_EQ_U64(p->StartingOffset.QuadPart, before.hdr.PartitionEntry[1].StartingOffset.QuadPart);
    ASSERT_EQ_U64(p->PartitionLength.QuadPart, before.hdr.PartitionEntry[1].PartitionLength.QuadPart);
    ASSERT_TRUE(memcmp(&p->Gpt.PartitionType, &before.hdr.PartitionEntry[1].Gpt.PartitionType, sizeof(GUID)) == 0);
    ASSERT_TRUE(memcmp(&p->Gpt.PartitionId, &before.hdr.PartitionEntry[1].Gpt.PartitionId, sizeof(GUID)) == 0);
    ASSERT_TRUE(memcmp(p->Gpt.Name, before.hdr.PartitionEntry[1].Gpt.Name, sizeof p->Gpt.Name) == 0);
    ASSERT_TRUE(memcmp(&b.hdr.PartitionEntry[0], &before.hdr.PartitionEntry[0], sizeof *p) == 0);
    ASSERT_TRUE(memcmp(&b.hdr.PartitionEntry[2], &before.hdr.PartitionEntry[2], sizeof *p) == 0);
    ASSERT_TRUE(memcmp(&b.hdr.Gpt, &before.hdr.Gpt, sizeof b.hdr.Gpt) == 0);

    /* Idempotent; restoring gives the original back. */
    before = b;
    ASSERT_EQ_INT(dmw_layout_set_attributes(&b.hdr, id, hidden, &old, &changed), DM_OK);
    ASSERT_EQ_INT(changed, 0);
    ASSERT_EQ_U64(old, hidden);
    ASSERT_TRUE(memcmp(&b, &before, sizeof b) == 0);
    ASSERT_EQ_INT(dmw_layout_set_attributes(&b.hdr, id, 0x8000000000000001ull, NULL, &changed), DM_OK);
    ASSERT_EQ_INT(changed, 1);
    ASSERT_EQ_U64(p->Gpt.Attributes, 0x8000000000000001ull);

    memset(id, 0x77, 16);
    ASSERT_EQ_INT(dmw_layout_set_attributes(&b.hdr, id, hidden, &old, &changed), DM_E_NOT_FOUND);
    memset(id, 0x41, 16);
    b.hdr.PartitionStyle = PARTITION_STYLE_MBR;
    ASSERT_EQ_INT(dmw_layout_set_attributes(&b.hdr, id, hidden, &old, &changed), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_layout_set_attributes(NULL, id, hidden, &old, &changed), DM_E_INVALID_ARG);
}

static void layout_gets_attributes(void)
{
    layout_buf b, before;
    make_layout(&b);
    before = b;
    uint8_t id[16]; memset(id, 0x41, 16);
    uint64_t a = 0;
    ASSERT_EQ_INT(dmw_layout_get_attributes(&b.hdr, id, &a), DM_OK);
    ASSERT_EQ_U64(a, 0x8000000000000001ull);
    ASSERT_TRUE(memcmp(&b, &before, sizeof b) == 0);   /* read-only */
    b.hdr.PartitionEntry[1].Gpt.Attributes = 0x1000000000000005ull;
    ASSERT_EQ_INT(dmw_layout_get_attributes(&b.hdr, id, &a), DM_OK);
    ASSERT_EQ_U64(a, 0x1000000000000005ull);
    memset(id, 0x77, 16);
    ASSERT_EQ_INT(dmw_layout_get_attributes(&b.hdr, id, &a), DM_E_NOT_FOUND);
    b.hdr.PartitionStyle = PARTITION_STYLE_MBR;
    ASSERT_EQ_INT(dmw_layout_get_attributes(&b.hdr, id, &a), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_layout_get_attributes(NULL, id, &a), DM_E_INVALID_ARG);
}

static void layout_detects_system_partition(void)
{
    static const GUID esp = { 0xC12A7328, 0xF81F, 0x11D2, { 0xBA, 0x4B, 0x00, 0xA0, 0xC9, 0x3E, 0xC9, 0x3B } };
    static const GUID basic = { 0xEBD0A0A2, 0xB9E5, 0x4433, { 0x87, 0xC0, 0x68, 0xB6, 0xB7, 0x26, 0x99, 0xC7 } };
    layout_buf b;
    make_layout(&b);
    for (int i = 0; i < 3; i++) b.hdr.PartitionEntry[i].Gpt.PartitionType = basic;
    ASSERT_EQ_INT(dmw_layout_has_system_partition(&b.hdr), 0);   /* data-only GPT disk (the VHDX case) */
    b.hdr.PartitionEntry[2].Gpt.PartitionType = esp;
    ASSERT_EQ_INT(dmw_layout_has_system_partition(&b.hdr), 1);   /* ESP anywhere */

    /* MBR: active partition, or type 0xEF. */
    memset(&b, 0, sizeof b);
    b.hdr.PartitionStyle = PARTITION_STYLE_MBR;
    b.hdr.PartitionCount = 2;
    for (int i = 0; i < 2; i++) {
        b.hdr.PartitionEntry[i].PartitionStyle = PARTITION_STYLE_MBR;
        b.hdr.PartitionEntry[i].Mbr.PartitionType = 0x07;
    }
    ASSERT_EQ_INT(dmw_layout_has_system_partition(&b.hdr), 0);
    b.hdr.PartitionEntry[1].Mbr.BootIndicator = TRUE;
    ASSERT_EQ_INT(dmw_layout_has_system_partition(&b.hdr), 1);
    b.hdr.PartitionEntry[1].Mbr.BootIndicator = FALSE;
    b.hdr.PartitionEntry[0].Mbr.PartitionType = 0xEF;
    ASSERT_EQ_INT(dmw_layout_has_system_partition(&b.hdr), 1);
    ASSERT_EQ_INT(dmw_layout_has_system_partition(NULL), 0);
}

/* Read-only smoke test on this machine: the disk holding the Windows volume (disk 0 here) is a system disk.
   Only queries volume extents; nothing is opened for data access, locked or written. */
static void disk_is_system_smoke(void)
{
    int is_system = 0;
    ASSERT_EQ_INT(dmw_disk_is_system(0, &is_system), DM_OK);
    ASSERT_EQ_INT(is_system, 1);
    ASSERT_EQ_INT(dmw_disk_is_system(0, NULL), DM_E_INVALID_ARG);
    /* A disk that does not exist: fails closed (error + is_system = 1). */
    is_system = 0;
    ASSERT_TRUE(dmw_disk_is_system(9999, &is_system) != DM_OK);
    ASSERT_EQ_INT(is_system, 1);
}

typedef union extents_buf {
    VOLUME_DISK_EXTENTS e;
    uint8_t raw[sizeof(VOLUME_DISK_EXTENTS) + sizeof(DISK_EXTENT)];
} extents_buf;

static void volume_extent_match(void)
{
    extents_buf b;
    memset(&b, 0, sizeof b);
    b.e.NumberOfDiskExtents = 1;
    b.e.Extents[0].DiskNumber = 3;
    b.e.Extents[0].StartingOffset.QuadPart = (LONGLONG)(1025 * MIB);
    b.e.Extents[0].ExtentLength.QuadPart = (LONGLONG)(512 * MIB);
    ASSERT_EQ_INT(dmw_extents_match(&b.e, 3, 1025 * MIB, 512 * MIB), DM_OK);
    ASSERT_EQ_INT(dmw_extents_match(&b.e, 2, 1025 * MIB, 512 * MIB), DM_E_INVALID_ARG);       /* other disk */
    ASSERT_EQ_INT(dmw_extents_match(&b.e, 3, 1024 * MIB, 512 * MIB), DM_E_INVALID_ARG);       /* other start */
    ASSERT_EQ_INT(dmw_extents_match(&b.e, 3, 1025 * MIB, 511 * MIB), DM_E_INVALID_ARG);       /* other length */
    ASSERT_EQ_INT(dmw_extents_match(&b.e, 3, 1025 * MIB, 512 * MIB + 4096), DM_E_INVALID_ARG);
    /* A spanned volume (2 extents) never matches, even if the first extent does. */
    b.e.NumberOfDiskExtents = 2;
    DISK_EXTENT *second = (DISK_EXTENT *)(b.raw + sizeof(VOLUME_DISK_EXTENTS));
    second->DiskNumber = 4;
    ASSERT_EQ_INT(dmw_extents_match(&b.e, 3, 1025 * MIB, 512 * MIB), DM_E_INVALID_ARG);
    b.e.NumberOfDiskExtents = 0;
    ASSERT_EQ_INT(dmw_extents_match(&b.e, 3, 1025 * MIB, 512 * MIB), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_extents_match(NULL, 3, 1025 * MIB, 512 * MIB), DM_E_INVALID_ARG);
}

/* The disk-only entry points refuse a plain file (no device IOCTLs) and leave it untouched. */
static void disk_only_calls_refuse_plain_file(void)
{
    wchar_t path[MAX_PATH], none[MAX_PATH];
    temp_path(path, L"plain");
    temp_path(none, L"none");
    uint8_t data[4096];
    memset(data, 0x5A, sizeof data);
    ASSERT_TRUE(write_file(path, data, sizeof data));
    uint8_t id[16]; memset(id, 0x41, 16);
    uint32_t n = 77;
    uint64_t old = 0;
    ASSERT_TRUE(dmw_disk_number(path, &n) != DM_OK);
    ASSERT_EQ_U32(n, 77);
    ASSERT_EQ_INT(dmw_disk_number(none, &n), DM_E_NOT_FOUND);
    ASSERT_TRUE(dmw_set_partition_attributes(path, id, DMW_GPT_ATTRIBUTE_HIDDEN, &old) != DM_OK);
    ASSERT_EQ_INT(dmw_set_partition_attributes(none, id, DMW_GPT_ATTRIBUTE_HIDDEN, &old), DM_E_NOT_FOUND);
    ASSERT_TRUE(dmw_set_partition_start(path, id, MIB) != DM_OK);
    ASSERT_TRUE(dmw_get_partition_attributes(path, id, &old) != DM_OK);
    uint64_t size = 0;
    uint8_t *got = read_file(path, &size);
    ASSERT_TRUE(got && size == sizeof data && memcmp(got, data, sizeof data) == 0);
    free(got);
    DeleteFileW(path);
}

/* ---- disk identity before the first write (pure part of dmw_run_move's check) ---- */

static void identity_plan(dm_move_plan *p)
{
    memset(p, 0, sizeof *p);
    memset(p->disk_guid, 0x55, 16);
    memset(p->partition_guid, 0x41, 16);   /* middle partition of make_layout */
    p->sector_size = 512;
    p->old_start = 1025 * MIB;
    p->length = 512 * MIB;
    p->shift = 512 * MIB;
}

static void layout_checks_move_identity(void)
{
    layout_buf b;
    dm_move_plan p;
    identity_plan(&p);
    make_layout(&b);
    memset(&b.hdr.Gpt.DiskId, 0x55, sizeof(GUID));
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_OK);                    /* not committed yet */
    b.hdr.PartitionEntry[1].StartingOffset.QuadPart = (LONGLONG)(1537 * MIB);
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_OK);                    /* already committed */
    b.hdr.PartitionEntry[1].StartingOffset.QuadPart = (LONGLONG)(1026 * MIB);
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_E_JOURNAL_MISMATCH);    /* elsewhere */

    make_layout(&b);
    memset(&b.hdr.Gpt.DiskId, 0x56, sizeof(GUID));
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_E_JOURNAL_MISMATCH);    /* another disk */
    memset(&b.hdr.Gpt.DiskId, 0x55, sizeof(GUID));
    b.hdr.PartitionEntry[1].PartitionLength.QuadPart = (LONGLONG)(511 * MIB);
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_E_JOURNAL_MISMATCH);    /* other length */
    make_layout(&b);
    memset(&b.hdr.Gpt.DiskId, 0x55, sizeof(GUID));
    memset(p.partition_guid, 0x77, 16);
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_E_JOURNAL_MISMATCH);    /* no such partition */
    identity_plan(&p);
    p.shift = UINT64_MAX;                                                       /* old_start + shift overflows */
    b.hdr.PartitionEntry[1].StartingOffset.QuadPart = (LONGLONG)(1024 * MIB);
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_E_JOURNAL_MISMATCH);
    identity_plan(&p);
    make_layout(&b);
    memset(&b.hdr.Gpt.DiskId, 0x55, sizeof(GUID));
    b.hdr.PartitionStyle = PARTITION_STYLE_MBR;
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_E_JOURNAL_MISMATCH);    /* not GPT */
    ASSERT_EQ_INT(dmw_layout_check_move(NULL, &p), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, NULL), DM_E_INVALID_ARG);
}

/* Review I1: the destination [old_start + shift, +length) must be inside the GPT usable range and must not
   overlap any other partition; the moved partition may overlap its own old range. */
static void layout_check_move_rejects_unsafe_destination(void)
{
    layout_buf b;
    dm_move_plan p;
    make_layout(&b);   /* #1 [1, 1025) MiB, #2 [1025, 1537), #3 [2049, 3073); usable end just under 4096 MiB */
    memset(&b.hdr.Gpt.DiskId, 0x55, sizeof(GUID));

    identity_plan(&p);                                                          /* clean: [1537, 2049) is free */
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_OK);
    p.shift = 256 * MIB;                                                        /* [1281, 1793): overlaps itself only */
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_OK);
    p.shift = 600 * MIB;                                                        /* [1625, 2137): overlaps #3 */
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_E_JOURNAL_MISMATCH);
    p.shift = 513 * MIB;                                                        /* [1538, 2050): 1 MiB into #3 */
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_E_JOURNAL_MISMATCH);

    /* Last partition: a destination past the usable end is refused, one inside it is fine. */
    memset(p.partition_guid, 0x42, 16);
    p.old_start = 2049 * MIB;
    p.length = 1024 * MIB;
    p.shift = 1024 * MIB;                                                       /* ends at 4097 MiB */
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_E_JOURNAL_MISMATCH);
    p.shift = 1000 * MIB;                                                       /* ends at 4073 MiB */
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_OK);

    /* First partition: its destination would land on #2. */
    memset(p.partition_guid, 0x40, 16);
    p.old_start = 1 * MIB;
    p.length = 1024 * MIB;
    p.shift = 1 * MIB;
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_E_JOURNAL_MISMATCH);

    /* Overflow of old_start + shift + length. */
    identity_plan(&p);
    p.shift = UINT64_MAX - p.old_start - 1;
    ASSERT_EQ_INT(dmw_layout_check_move(&b.hdr, &p), DM_E_JOURNAL_MISMATCH);
}

/* The exported dmw_run_move checks the disk before its first write: a plain file has no GPT, so it is
   refused and stays byte-identical, and the journal shows no progress. */
static void run_move_refuses_unverified_disk(void)
{
    dm_move_plan p; dm_extent e;
    small_plan(&p, &e);
    uint64_t size = p.old_start + p.length + p.shift + MIB;
    uint8_t *data = (uint8_t *)malloc((size_t)size);
    ASSERT_TRUE(data != NULL);
    if (!data) return;
    for (uint64_t i = 0; i < size; i++) data[i] = (uint8_t)(i * 31 + 7);
    wchar_t disk[MAX_PATH], jr[MAX_PATH];
    temp_path(disk, L"ident");
    temp_path(jr, L"identjr");
    ASSERT_TRUE(write_file(disk, data, size));
    ASSERT_EQ_INT(dmw_journal_create_ex(jr, &p, 1, NULL, 0, 0), DM_OK);
    ASSERT_TRUE(dmw_run_move(disk, jr, 0, NULL, NULL, NULL) != DM_OK);
    uint64_t got = 0;
    uint8_t *after = read_file(disk, &got);
    ASSERT_TRUE(after && got == size && memcmp(after, data, (size_t)size) == 0);
    dm_journal_info info; uint32_t alen = 0;
    ASSERT_EQ_INT(dmw_journal_read(jr, &info, NULL, 0, &alen), DM_OK);
    ASSERT_EQ_U64(info.chunks_done, 0);
    free(after);
    free(data);
    DeleteFileW(disk);
    DeleteFileW(jr);
}


/* ---- journal v2 / old-offset volume (boot-sector fix) ---- */

/* The DLL refuses a plan without saved boot sectors BEFORE CREATE_ALWAYS: an existing journal is not truncated. */
static void journal_create_requires_boot_sectors(void)
{
    wchar_t path[MAX_PATH];
    temp_path(path, L"jrboot");
    uint8_t keep[8192];
    memset(keep, 0x3C, sizeof keep);
    ASSERT_TRUE(write_file(path, keep, sizeof keep));
    dm_move_plan p; dm_extent e;
    small_plan(&p, &e);
    p.boot_backup = NULL;
    ASSERT_EQ_INT(dmw_journal_create_ex(path, &p, 1, NULL, 0, 0), DM_E_INVALID_ARG);
    p.boot_backup = k_boot[1];
    p.boot_first = NULL;
    ASSERT_EQ_INT(dmw_journal_create(path, &p, 1, NULL, 0), DM_E_INVALID_ARG);
    uint64_t size = 0;
    uint8_t *got = read_file(path, &size);
    ASSERT_TRUE(got && size == sizeof keep && memcmp(got, keep, sizeof keep) == 0);
    free(got);
    DeleteFileW(path);
}

/* dmw_read_boot_sectors: exactly the first and the last logical sector of [start, start + length); read-only. */
static void read_boot_sectors_on_file(void)
{
    uint64_t size = 8 * MIB;
    uint8_t *img = (uint8_t *)malloc((size_t)size);
    ASSERT_TRUE(img != NULL);
    if (!img) return;
    for (uint64_t i = 0; i < size; i++) img[i] = (uint8_t)(i * 7 + (i >> 9));
    wchar_t path[MAX_PATH];
    temp_path(path, L"bootrd");
    ASSERT_TRUE(write_file(path, img, size));
    static uint8_t first[4096], backup[4096];
    for (uint32_t sector = 512; sector <= 4096; sector *= 8) {
        memset(first, 0xEE, sizeof first); memset(backup, 0xEE, sizeof backup);
        ASSERT_EQ_INT(dmw_read_boot_sectors(path, 1 * MIB, 4 * MIB, sector, first, backup), DM_OK);
        ASSERT_TRUE(memcmp(first, img + 1 * MIB, sector) == 0);
        ASSERT_TRUE(memcmp(backup, img + 5 * MIB - sector, sector) == 0);
        if (sector < 4096) ASSERT_EQ_U32(first[sector], 0xEE);
    }
    ASSERT_EQ_INT(dmw_read_boot_sectors(path, 1 * MIB + 100, 4 * MIB, 512, first, backup), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_read_boot_sectors(path, 1 * MIB, 4 * MIB, 1024, first, backup), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_read_boot_sectors(path, 1 * MIB, 0, 512, first, backup), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_read_boot_sectors(path, 1 * MIB, 4 * MIB, 512, NULL, backup), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_read_boot_sectors(NULL, 1 * MIB, 4 * MIB, 512, first, backup), DM_E_INVALID_ARG);
    uint64_t got_size = 0;
    uint8_t *after = read_file(path, &got_size);
    ASSERT_TRUE(after && got_size == size && memcmp(after, img, (size_t)size) == 0);
    free(after);
    free(img);
    DeleteFileW(path);
}

/* Fake \Device\HarddiskVolumeN table for the pure scan (dmw_find_volume_device). */
typedef struct fake_vol { uint32_t n, extents, disk; uint64_t start, length; dm_status err; } fake_vol;
typedef struct fake_vols { const fake_vol *v; uint32_t count, calls, max_n; } fake_vols;

static dm_status __cdecl fake_query(void *ctx, uint32_t n, VOLUME_DISK_EXTENTS *buf, uint32_t size)
{
    fake_vols *f = (fake_vols *)ctx;
    f->calls++;
    if (n > f->max_n) f->max_n = n;
    for (uint32_t i = 0; i < f->count; i++) {
        const fake_vol *v = &f->v[i];
        if (v->n != n) continue;
        if (v->err != DM_OK) return v->err;
        if (size < sizeof(VOLUME_DISK_EXTENTS) + sizeof(DISK_EXTENT)) return DM_E_INVALID_ARG;
        memset(buf, 0, size);
        buf->NumberOfDiskExtents = v->extents;
        for (uint32_t k = 0; k < v->extents && k < 2; k++) {
            DISK_EXTENT *e = &buf->Extents[0] + k;
            e->DiskNumber = v->disk;
            e->StartingOffset.QuadPart = (LONGLONG)v->start;
            e->ExtentLength.QuadPart = (LONGLONG)v->length;
        }
        return DM_OK;
    }
    return DM_E_NOT_FOUND;   /* no such device */
}

static void old_volume_scan_matches_exact_extent(void)
{
    const uint64_t start = 1405091840ull, len = 1610612736ull;
    const fake_vol vols[] = {
        { 1, 1, 0, 1 * MIB, 100 * MIB, DM_OK },             /* the system disk */
        { 7, 1, 3, start, len + 4096, DM_OK },              /* other length */
        { 9, 1, 2, start, len, DM_OK },                     /* other disk */
        { 30, 1, 3, start + 4096, len, DM_OK },             /* other start */
        { 31, 2, 3, start, len, DM_OK },                    /* spanned: never */
        { 40, 1, 3, start, len, DM_E_ACCESS },              /* query fails: skipped */
        { 412, 1, 3, start, len, DM_OK },                   /* the stale old-offset device of the investigation */
        { 500, 1, 3, start, len, DM_OK },                   /* a later duplicate: the first match wins */
    };
    fake_vols f = { vols, (uint32_t)(sizeof vols / sizeof vols[0]), 0, 0 };
    uint32_t found = 0;
    ASSERT_EQ_INT(dmw_find_volume_device(fake_query, &f, 4096, 3, start, len, &found), DM_OK);
    ASSERT_EQ_U32(found, 412);
    ASSERT_EQ_U32(f.max_n, 412);                            /* stops at the first match */
    ASSERT_EQ_U32(f.calls, 412);                            /* N = 1..412, none skipped */

    /* nothing matches: DM_E_NOT_FOUND after the whole range */
    f.calls = f.max_n = 0;
    ASSERT_EQ_INT(dmw_find_volume_device(fake_query, &f, 4096, 3, start, len - 4096, &found), DM_E_NOT_FOUND);
    ASSERT_EQ_U32(f.calls, 4096);
    /* the range limit is honoured (412 is beyond it) */
    f.calls = f.max_n = 0;
    ASSERT_EQ_INT(dmw_find_volume_device(fake_query, &f, 256, 3, start, len, &found), DM_E_NOT_FOUND);
    ASSERT_EQ_U32(f.max_n, 256);
    ASSERT_EQ_INT(dmw_find_volume_device(NULL, &f, 256, 3, start, len, &found), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_find_volume_device(fake_query, &f, 256, 3, start, len, NULL), DM_E_INVALID_ARG);
    ASSERT_TRUE(DMW_VOLUME_SCAN_MAX >= 1024);               /* the investigation saw HarddiskVolume412 */
}

/* The real scan over \\?\GLOBALROOT\Device\HarddiskVolumeN with zero-access opens (no mount, no lock, no write):
   no volume is on a disk number that does not exist. Runs without admin. */
static void old_volume_scan_smoke(void)
{
    LARGE_INTEGER t0, t1, fq;
    QueryPerformanceFrequency(&fq);
    QueryPerformanceCounter(&t0);
    uint32_t found = 0;
    ASSERT_EQ_INT(dmw_find_volume_device(dmw_query_volume_device, NULL, DMW_VOLUME_SCAN_MAX, 0xFFFFu, 1 * MIB, 1 * MIB, &found),
                  DM_E_NOT_FOUND);
    QueryPerformanceCounter(&t1);
    printf("  old_volume_scan_smoke: %u devices scanned in %.1f ms\n", (unsigned)DMW_VOLUME_SCAN_MAX,
           (double)(t1.QuadPart - t0.QuadPart) * 1000.0 / (double)fq.QuadPart);
    /* The scan does see real devices: it finds the Windows volume by its own extent (zero-access opens only). */
    wchar_t win[MAX_PATH], root[4] = L"C:\\", dev[8] = L"\\\\.\\C:";
    if (GetSystemWindowsDirectoryW(win, MAX_PATH)) { root[0] = win[0]; dev[4] = win[0]; }
    HANDLE h = CreateFileW(dev, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    ASSERT_TRUE(h != INVALID_HANDLE_VALUE);
    union { VOLUME_DISK_EXTENTS e; uint8_t raw[sizeof(VOLUME_DISK_EXTENTS) + 7 * sizeof(DISK_EXTENT)]; } b;
    DWORD ret = 0;
    if (h != INVALID_HANDLE_VALUE &&
        DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, NULL, 0, &b, sizeof b, &ret, NULL) &&
        b.e.NumberOfDiskExtents == 1) {
        const DISK_EXTENT *e = &b.e.Extents[0];
        ASSERT_EQ_INT(dmw_find_volume_device(dmw_query_volume_device, NULL, DMW_VOLUME_SCAN_MAX, e->DiskNumber,
                                             (uint64_t)e->StartingOffset.QuadPart, (uint64_t)e->ExtentLength.QuadPart,
                                             &found), DM_OK);
        printf("  old_volume_scan_smoke: the Windows volume %ls is HarddiskVolume%u\n", root, found);
    }
    if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    dmw_volume *v = (dmw_volume *)1;
    ASSERT_EQ_INT(dmw_lock_old_volume(0xFFFFu, 1 * MIB, 1 * MIB, &v), DM_OK);   /* none found: nothing to lock */
    ASSERT_TRUE(v == NULL);
    ASSERT_EQ_INT(dmw_lock_old_volume(0xFFFFu, 1 * MIB, 1 * MIB, NULL), DM_E_INVALID_ARG);
}

static void misc_args(void)
{
    dmw_volume *v = (dmw_volume *)1;
    ASSERT_EQ_INT(dmw_volume_lock(NULL, &v), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_volume_lock(L"\\\\.\\X:", NULL), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_volume_dismount(NULL), DM_E_INVALID_ARG);
    dmw_volume_close(NULL);
    ASSERT_EQ_INT(dmw_free(NULL), DM_OK);
    uint8_t id[16] = { 0 };
    ASSERT_EQ_INT(dmw_set_partition_start(NULL, id, MIB), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_set_partition_attributes(NULL, id, 0, NULL), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_set_partition_attributes(L"x", NULL, 0, NULL), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_volume_verify(NULL, 0, 0, MIB), DM_E_INVALID_ARG);
    uint32_t dn = 0;
    ASSERT_EQ_INT(dmw_disk_number(NULL, &dn), DM_E_INVALID_ARG);
    uint64_t at = 0;
    ASSERT_EQ_INT(dmw_get_partition_attributes(NULL, id, &at), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_get_partition_attributes(L"x", id, NULL), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_disk_number(L"x", NULL), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_journal_create(NULL, NULL, 0, NULL, 0), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dmw_status_from_win32(ERROR_ACCESS_DENIED), DM_E_ACCESS);
    ASSERT_EQ_INT(dmw_status_from_win32(ERROR_FILE_NOT_FOUND), DM_E_NOT_FOUND);
    ASSERT_EQ_INT(dmw_status_from_win32(ERROR_PATH_NOT_FOUND), DM_E_NOT_FOUND);
    ASSERT_EQ_INT(dmw_status_from_win32(ERROR_CRC), DM_E_IO);
}

void dm_run_win32_io(void)
{
    DM_RUN(journal_roundtrip_truncates_old_file);
    DM_RUN(journal_acl_admin_system_only);
    DM_RUN(journal_missing_file);
    DM_RUN(move_basic_on_file);
    DM_RUN(move_4kn_on_file);
    DM_RUN(move_stop_and_missing_paths);
    DM_RUN(patch_hidden_sectors_512);
    DM_RUN(patch_hidden_sectors_4096);
    DM_RUN(patch_skips_non_ntfs_backup);
    DM_RUN(patch_rejects_and_skips);
    DM_RUN(layout_moves_partition);
    DM_RUN(layout_rejects_bad_moves);
    DM_RUN(layout_sets_attributes);
    DM_RUN(layout_gets_attributes);
    DM_RUN(layout_detects_system_partition);
    DM_RUN(disk_is_system_smoke);
    DM_RUN(volume_extent_match);
    DM_RUN(disk_only_calls_refuse_plain_file);
    DM_RUN(layout_checks_move_identity);
    DM_RUN(layout_check_move_rejects_unsafe_destination);
    DM_RUN(run_move_refuses_unverified_disk);
    DM_RUN(misc_args);
    DM_RUN(journal_create_requires_boot_sectors);
    DM_RUN(read_boot_sectors_on_file);
    DM_RUN(old_volume_scan_matches_exact_extent);
    DM_RUN(old_volume_scan_smoke);
}
