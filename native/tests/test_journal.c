#include "dm_test.h"
#include "mem_io.h"
#include <diskmove/dm_core.h>
#include <stdlib.h>
#include <string.h>

#define KIB 1024ull
#define MIB (1024ull * KIB)
#define GIB (1024ull * MIB)

/* Journal v2 stores each move's original first and last (backup boot) sector. */
static uint8_t k_boot_first[4096], k_boot_backup[4096];

static void boot_fill(void)
{
    for (int i = 0; i < 4096; i++) { k_boot_first[i] = (uint8_t)(i * 13 + 5); k_boot_backup[i] = (uint8_t)(i * 29 + 11); }
}

static dm_move_plan mk_plan(const dm_extent *ex, uint32_t n, uint8_t tag)
{
    dm_move_plan p;
    boot_fill();
    memset(&p, 0, sizeof p);
    memset(p.disk_guid, tag, 16);
    memset(p.partition_guid, tag + 1, 16);
    p.sector_size = 512;
    p.old_start = 1 * MIB;
    p.length = 64 * MIB;
    p.shift = 32 * MIB;
    p.extent_count = n;
    p.extents = ex;
    p.boot_first = k_boot_first;
    p.boot_backup = k_boot_backup;
    return p;
}

static void test_journal_roundtrip(void)
{
    dm_extent e0[2] = { { 0, 3 * MIB }, { 10 * MIB, 40 * MIB } };
    dm_extent e1[1] = { { 5 * MIB, 1 * MIB } };
    dm_move_plan plans[2] = { mk_plan(e0, 2, 0x10), mk_plan(e1, 1, 0x20) };
    plans[1].shift = 8 * MIB;
    uint8_t blob[300], back[300];
    for (int i = 0; i < 300; i++) blob[i] = (uint8_t)(i * 7 + 1);

    mem_io m; mem_io_init(&m, NULL);
    dm_io io = mem_io_as_dm_io(&m);
    ASSERT_EQ_INT(dm_journal_create(&io, plans, 2, blob, 300), DM_OK);

    dm_journal_info info; uint32_t alen = 0;
    ASSERT_EQ_INT(dm_journal_read(&io, &info, back, sizeof back, &alen), DM_OK);
    ASSERT_EQ_U32(info.move_count, 2);
    ASSERT_EQ_U32(info.step, 0);
    ASSERT_EQ_U64(info.seq, 1);
    ASSERT_EQ_U32(alen, 300);
    ASSERT_TRUE(memcmp(blob, back, 300) == 0);

    /* too-small buffer: length still reported */
    alen = 0;
    ASSERT_EQ_INT(dm_journal_read(&io, &info, back, 10, &alen), DM_E_INVALID_ARG);
    ASSERT_EQ_U32(alen, 300);

    dm_extent *want = NULL; uint32_t want_n = 0;
    ASSERT_EQ_INT(dm_chunks_build(&plans[1], &want, &want_n), DM_OK);
    ASSERT_EQ_U64(info.chunk_count[1], want_n);

    dm_move_plan out; dm_extent *ch = NULL; uint32_t n = 0;
    ASSERT_EQ_INT(dm_journal_load_move(&io, 1, &out, &ch, &n), DM_OK);
    ASSERT_EQ_U32(n, want_n);
    ASSERT_TRUE(n > 0 && memcmp(ch, want, n * sizeof *ch) == 0);
    ASSERT_TRUE(out.extents == NULL && out.extent_count == 0);
    ASSERT_EQ_U64(out.shift, 8 * MIB);
    ASSERT_EQ_U64(out.old_start, plans[1].old_start);
    ASSERT_EQ_U64(out.length, plans[1].length);
    ASSERT_EQ_U32(out.sector_size, 512);
    ASSERT_TRUE(memcmp(out.disk_guid, plans[1].disk_guid, 16) == 0);
    ASSERT_TRUE(memcmp(out.partition_guid, plans[1].partition_guid, 16) == 0);
    dm_free(ch); dm_free(want);

    ASSERT_EQ_INT(dm_journal_load_move(&io, 2, &out, &ch, &n), DM_E_INVALID_ARG);

    ASSERT_EQ_INT(dm_journal_progress(&io, 1, 3, 0xABCDEFull), DM_OK);
    ASSERT_EQ_INT(dm_journal_read(&io, &info, NULL, 0, &alen), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dm_journal_read(&io, &info, back, sizeof back, &alen), DM_OK);
    ASSERT_EQ_U32(info.move_index, 1);
    ASSERT_EQ_U64(info.chunks_done, 3);
    ASSERT_EQ_U64(info.seq, 2);
    mem_io_free(&m);
}

static void test_journal_slots_alternate(void)
{
    dm_extent e = { 0, 1 * MIB };
    dm_move_plan p = mk_plan(&e, 1, 1);
    mem_io m; mem_io_init(&m, NULL);
    dm_io io = mem_io_as_dm_io(&m);
    ASSERT_EQ_INT(dm_journal_create(&io, &p, 1, NULL, 0), DM_OK);
    for (uint32_t s = 1; s <= 5; s++) ASSERT_EQ_INT(dm_journal_set_step(&io, s), DM_OK);
    dm_journal_info info; uint32_t alen = 99;
    ASSERT_EQ_INT(dm_journal_read(&io, &info, NULL, 0, &alen), DM_OK);
    ASSERT_EQ_U64(info.seq, 6);
    ASSERT_EQ_U32(info.step, 5);
    ASSERT_EQ_U32(alen, 0);
    mem_io_free(&m);
}

static void test_journal_torn_slot_write(void)
{
    dm_extent e = { 0, 1 * MIB };
    dm_move_plan p = mk_plan(&e, 1, 1);
    mem_fault f; memset(&f, 0, sizeof f);
    mem_io m; mem_io_init(&m, &f);
    dm_io io = mem_io_as_dm_io(&m);
    ASSERT_EQ_INT(dm_journal_create(&io, &p, 1, NULL, 0), DM_OK);   /* writes 1 (clear slots), 2 (header), 3 (slot A) */
    ASSERT_EQ_INT(dm_journal_set_step(&io, 1), DM_OK);              /* write 3 (slot B) */
    ASSERT_EQ_INT(dm_journal_progress(&io, 0, 4, 77), DM_OK);       /* write 4 (slot A) */
    f.fail_after_writes = f.writes + 1;                             /* tear the next slot write */
    ASSERT_EQ_INT(dm_journal_set_step(&io, 2), DM_E_IO);
    ASSERT_TRUE(f.tripped);
    ASSERT_EQ_INT(dm_journal_set_step(&io, 3), DM_E_IO);

    dm_journal_info info; uint32_t alen;
    ASSERT_EQ_INT(dm_journal_read(&io, &info, NULL, 0, &alen), DM_OK);
    ASSERT_EQ_U32(info.step, 1);
    ASSERT_EQ_U64(info.chunks_done, 4);
    ASSERT_EQ_U64(info.seq, 3);

    /* recovery on a healthy io over the same bytes: the invalid slot is reused */
    mem_io m2; mem_io_init(&m2, NULL);
    m2.data = m.data; m2.size = m.size; m2.cap = m.cap;
    dm_io io2 = mem_io_as_dm_io(&m2);
    ASSERT_EQ_INT(dm_journal_set_step(&io2, 2), DM_OK);
    ASSERT_EQ_INT(dm_journal_read(&io2, &info, NULL, 0, &alen), DM_OK);
    ASSERT_EQ_U32(info.step, 2);
    ASSERT_EQ_U64(info.chunks_done, 4);
    ASSERT_EQ_U64(info.seq, 4);
    m.data = m2.data; m.size = m2.size; m.cap = m2.cap;
    mem_io_free(&m);
}

static void test_journal_corrupt_header(void)
{
    dm_extent e = { 0, 1 * MIB };
    dm_move_plan p = mk_plan(&e, 1, 1);
    mem_io m; mem_io_init(&m, NULL);
    dm_io io = mem_io_as_dm_io(&m);
    uint8_t blob[16] = { 1, 2, 3 };
    ASSERT_EQ_INT(dm_journal_create(&io, &p, 1, blob, sizeof blob), DM_OK);
    m.data[40] ^= 0x01;   /* inside the app blob / plan area */
    dm_journal_info info; uint32_t alen;
    ASSERT_EQ_INT(dm_journal_read(&io, &info, NULL, 0, &alen), DM_E_JOURNAL_CORRUPT);
    m.data[40] ^= 0x01;
    ASSERT_EQ_INT(dm_journal_read(&io, &info, blob, sizeof blob, &alen), DM_OK);
    m.data[0] ^= 0x01;    /* magic */
    ASSERT_EQ_INT(dm_journal_read(&io, &info, blob, sizeof blob, &alen), DM_E_JOURNAL_CORRUPT);
    mem_io_free(&m);

    /* empty / never-written journal */
    mem_io_init(&m, NULL);
    io = mem_io_as_dm_io(&m);
    ASSERT_EQ_INT(dm_journal_read(&io, &info, NULL, 0, &alen), DM_E_JOURNAL_CORRUPT);
    mem_io_free(&m);
}

static void test_journal_rejects_bad_args(void)
{
    dm_extent e = { 0, 1 * MIB };
    dm_move_plan p[3];
    p[0] = p[1] = p[2] = mk_plan(&e, 1, 1);
    mem_io m; mem_io_init(&m, NULL);
    dm_io io = mem_io_as_dm_io(&m);
    ASSERT_EQ_INT(dm_journal_create(&io, p, 0, NULL, 0), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dm_journal_create(&io, p, 3, NULL, 0), DM_E_INVALID_ARG);
    p[0].shift = 0;
    ASSERT_EQ_INT(dm_journal_create(&io, p, 1, NULL, 0), DM_E_INVALID_PLAN);
    p[0] = mk_plan(&e, 1, 1);
    p[0].boot_first = NULL;                                     /* v2: the saved boot sectors are required */
    ASSERT_EQ_INT(dm_journal_create(&io, p, 1, NULL, 0), DM_E_INVALID_ARG);
    p[0] = mk_plan(&e, 1, 1);
    p[0].boot_backup = NULL;
    ASSERT_EQ_INT(dm_journal_create(&io, p, 1, NULL, 0), DM_E_INVALID_ARG);
    ASSERT_EQ_U64(m.size, 0);                                   /* nothing written */
    mem_io_free(&m);
}

/* v2: each move record stores the saved first/backup sectors (sector_size bytes each); the header CRC covers them. */
static void test_journal_boot_sectors_roundtrip(void)
{
    dm_extent e0 = { 0, 1 * MIB }, e1 = { 0, 2 * MIB };
    dm_move_plan plans[2] = { mk_plan(&e0, 1, 0x10), mk_plan(&e1, 1, 0x20) };
    static uint8_t f1[4096], b1[4096];
    for (int i = 0; i < 4096; i++) { f1[i] = (uint8_t)(0xA0 ^ i); b1[i] = (uint8_t)(0x5C + i * 3); }
    plans[1].sector_size = 4096;
    plans[1].boot_first = f1;
    plans[1].boot_backup = b1;
    mem_io m; mem_io_init(&m, NULL);
    dm_io io = mem_io_as_dm_io(&m);
    ASSERT_EQ_INT(dm_journal_create(&io, plans, 2, "ab", 2), DM_OK);

    uint8_t first[4096], backup[4096];
    memset(first, 0xEE, sizeof first); memset(backup, 0xEE, sizeof backup);
    ASSERT_EQ_INT(dm_journal_load_boot(&io, 0, first, backup), DM_OK);
    ASSERT_TRUE(memcmp(first, k_boot_first, 512) == 0);
    ASSERT_TRUE(memcmp(backup, k_boot_backup, 512) == 0);
    ASSERT_EQ_U32(first[512], 0xEE);                            /* only sector_size bytes are written */
    ASSERT_EQ_INT(dm_journal_load_boot(&io, 1, first, backup), DM_OK);
    ASSERT_TRUE(memcmp(first, f1, 4096) == 0);
    ASSERT_TRUE(memcmp(backup, b1, 4096) == 0);
    ASSERT_EQ_INT(dm_journal_load_boot(&io, 2, first, backup), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dm_journal_load_boot(&io, 0, NULL, backup), DM_E_INVALID_ARG);

    /* the chunks still load behind the boot sectors */
    dm_move_plan out; dm_extent *ch = NULL; uint32_t n = 0;
    ASSERT_EQ_INT(dm_journal_load_move(&io, 1, &out, &ch, &n), DM_OK);
    ASSERT_EQ_U32(out.sector_size, 4096);
    ASSERT_TRUE(n > 0 && ch[n - 1].offset == 0);
    dm_free(ch);

    /* a flipped byte inside move 0's saved backup sector: header CRC mismatch */
    uint64_t off = 24 + 2 + 68 + 512 + 100;
    m.data[off] ^= 0x01;
    dm_journal_info info; uint32_t alen;
    char app[4];
    ASSERT_EQ_INT(dm_journal_read(&io, &info, app, sizeof app, &alen), DM_E_JOURNAL_CORRUPT);
    ASSERT_EQ_INT(dm_journal_load_boot(&io, 0, first, backup), DM_E_JOURNAL_CORRUPT);
    m.data[off] ^= 0x01;
    ASSERT_EQ_INT(dm_journal_read(&io, &info, app, sizeof app, &alen), DM_OK);
    mem_io_free(&m);
}

/* A v1 journal (version field 1) is rejected, never interpreted with the v2 layout. */
static void test_journal_rejects_v1(void)
{
    dm_extent e = { 0, 1 * MIB };
    dm_move_plan p = mk_plan(&e, 1, 1);
    mem_io m; mem_io_init(&m, NULL);
    dm_io io = mem_io_as_dm_io(&m);
    ASSERT_EQ_INT(dm_journal_create(&io, &p, 1, NULL, 0), DM_OK);
    ASSERT_EQ_U32(m.data[8] | m.data[9] << 8 | m.data[10] << 16 | (uint32_t)m.data[11] << 24, 2);
    m.data[8] = 1;
    dm_journal_info info; uint32_t alen;
    ASSERT_EQ_INT(dm_journal_read(&io, &info, NULL, 0, &alen), DM_E_JOURNAL_CORRUPT);
    ASSERT_EQ_INT(dm_journal_set_step(&io, 3), DM_E_JOURNAL_CORRUPT);
    ASSERT_EQ_INT(dm_journal_progress(&io, 0, 1, 0), DM_E_JOURNAL_CORRUPT);
    dm_move_plan out; dm_extent *ch = NULL; uint32_t n = 0;
    ASSERT_EQ_INT(dm_journal_load_move(&io, 0, &out, &ch, &n), DM_E_JOURNAL_CORRUPT);
    mem_io_free(&m);
}

/* The slot's boot_restored flag: set by dm_journal_boot_restored, cleared by progress, kept by set_step. */
static void test_journal_boot_restored_flag(void)
{
    dm_extent e = { 0, 1 * MIB };
    dm_move_plan p = mk_plan(&e, 1, 1);
    mem_io m; mem_io_init(&m, NULL);
    dm_io io = mem_io_as_dm_io(&m);
    ASSERT_EQ_INT(dm_journal_create(&io, &p, 1, NULL, 0), DM_OK);
    dm_journal_info info; uint32_t alen;
    ASSERT_EQ_INT(dm_journal_read(&io, &info, NULL, 0, &alen), DM_OK);
    ASSERT_EQ_U32(info.boot_restored, 0);
    ASSERT_EQ_INT(dm_journal_progress(&io, 0, 5, 9), DM_OK);
    ASSERT_EQ_INT(dm_journal_boot_restored(&io, 0), DM_OK);
    ASSERT_EQ_INT(dm_journal_read(&io, &info, NULL, 0, &alen), DM_OK);
    ASSERT_EQ_U32(info.boot_restored, 1);
    ASSERT_EQ_U64(info.chunks_done, 5);
    ASSERT_EQ_INT(dm_journal_set_step(&io, 4), DM_OK);
    ASSERT_EQ_INT(dm_journal_read(&io, &info, NULL, 0, &alen), DM_OK);
    ASSERT_EQ_U32(info.boot_restored, 1);
    ASSERT_EQ_U32(info.step, 4);
    ASSERT_EQ_INT(dm_journal_progress(&io, 0, 5, 9), DM_OK);
    ASSERT_EQ_INT(dm_journal_read(&io, &info, NULL, 0, &alen), DM_OK);
    ASSERT_EQ_U32(info.boot_restored, 0);
    ASSERT_EQ_INT(dm_journal_boot_restored(&io, 1), DM_E_INVALID_ARG);   /* only the slot's current move */
    mem_io_free(&m);
}

static void test_journal_200k_extents(void)
{
    enum { N = 200000 };
    dm_extent *ex = (dm_extent *)malloc(N * sizeof *ex);
    ASSERT_TRUE(ex != NULL);
    if (!ex) return;
    for (uint32_t i = 0; i < N; i++) { ex[i].offset = (uint64_t)i * 8192; ex[i].length = 4096; }
    dm_move_plan p = mk_plan(ex, N, 3);
    p.length = 2 * GIB;
    dm_extent *want = NULL; uint32_t want_n = 0;
    ASSERT_EQ_INT(dm_chunks_build(&p, &want, &want_n), DM_OK);
    ASSERT_TRUE(want_n >= 100000);

    mem_io m; mem_io_init(&m, NULL);
    dm_io io = mem_io_as_dm_io(&m);
    ASSERT_EQ_INT(dm_journal_create(&io, &p, 1, NULL, 0), DM_OK);
    dm_journal_info info; uint32_t alen;
    ASSERT_EQ_INT(dm_journal_read(&io, &info, NULL, 0, &alen), DM_OK);
    ASSERT_EQ_U64(info.chunk_count[0], want_n);
    ASSERT_EQ_U64(info.chunk_count[1], 0);

    dm_move_plan out; dm_extent *ch = NULL; uint32_t n = 0;
    ASSERT_EQ_INT(dm_journal_load_move(&io, 0, &out, &ch, &n), DM_OK);
    ASSERT_EQ_U32(n, want_n);
    ASSERT_TRUE(ch && memcmp(ch, want, (size_t)n * sizeof *ch) == 0);
    dm_free(ch); dm_free(want); free(ex);
    mem_io_free(&m);
}

static void prefill_old_journal(mem_io *m, const dm_move_plan *p)
{
    dm_io io = mem_io_as_dm_io(m);
    ASSERT_EQ_INT(dm_journal_create(&io, p, 1, NULL, 0), DM_OK);
    ASSERT_EQ_INT(dm_journal_set_step(&io, 4), DM_OK);
    ASSERT_EQ_INT(dm_journal_progress(&io, 0, 9, 0x1234), DM_OK);   /* newest slot: seq 3, chunks_done 9 */
    ASSERT_EQ_INT(dm_journal_progress(&io, 0, 11, 0x5678), DM_OK);  /* seq 4 -> slot B holds the higher seq */
}

static void test_journal_stale_slot_from_previous_journal(void)
{
    dm_extent e = { 0, 1 * MIB };
    for (int variant = 0; variant < 2; variant++) {   /* 0: identical header, 1: different header */
        dm_move_plan oldp = mk_plan(&e, 1, 1);
        dm_move_plan newp = oldp;
        if (variant) newp.shift = 16 * MIB;
        mem_io m; mem_io_init(&m, NULL);
        prefill_old_journal(&m, &oldp);
        dm_io io = mem_io_as_dm_io(&m);
        ASSERT_EQ_INT(dm_journal_create(&io, &newp, 1, NULL, 0), DM_OK);
        dm_journal_info info; uint32_t alen;
        ASSERT_EQ_INT(dm_journal_read(&io, &info, NULL, 0, &alen), DM_OK);
        ASSERT_EQ_U64(info.seq, 1);
        ASSERT_EQ_U64(info.chunks_done, 0);
        ASSERT_EQ_U32(info.step, 0);
        mem_io_free(&m);
    }
}

static void test_journal_stale_slot_not_bound_to_other_header(void)
{
    /* an old slot written under a different header must not validate under a new header of equal size */
    dm_extent e = { 0, 1 * MIB };
    dm_move_plan oldp = mk_plan(&e, 1, 1), newp = oldp;
    newp.shift = 16 * MIB;
    mem_io a, b; mem_io_init(&a, NULL); mem_io_init(&b, NULL);
    prefill_old_journal(&a, &oldp);
    dm_io ioa = mem_io_as_dm_io(&a), iob = mem_io_as_dm_io(&b);
    ASSERT_EQ_INT(dm_journal_create(&iob, &newp, 1, NULL, 0), DM_OK);
    ASSERT_TRUE(a.size == b.size);
    memcpy(b.data + 4096, a.data + 4096, (size_t)(a.size - 4096));   /* new header, old slots */
    dm_journal_info info; uint32_t alen;
    ASSERT_EQ_INT(dm_journal_read(&iob, &info, NULL, 0, &alen), DM_E_JOURNAL_CORRUPT);
    (void)ioa;
    mem_io_free(&a); mem_io_free(&b);
}

static void test_journal_crash_between_header_and_slot(void)
{
    dm_extent e = { 0, 1 * MIB };
    /* create issues 3 writes: clear slots (1), header (2), slot A (3) */
    for (uint64_t fail_at = 1; fail_at <= 3; fail_at++) {
        dm_move_plan oldp = mk_plan(&e, 1, 1);
        mem_io m; mem_io_init(&m, NULL);
        prefill_old_journal(&m, &oldp);
        mem_fault f; memset(&f, 0, sizeof f);
        f.fail_after_writes = fail_at;
        m.fault = &f;
        dm_io io = mem_io_as_dm_io(&m);
        dm_move_plan newp = oldp; newp.shift = 16 * MIB;
        ASSERT_EQ_INT(dm_journal_create(&io, &newp, 1, NULL, 0), DM_E_IO);
        ASSERT_TRUE(f.tripped);
        dm_journal_info info; uint32_t alen;
        dm_status rs = dm_journal_read(&io, &info, NULL, 0, &alen);
        /* fail_at 1: crash before the header changed -> the intact old journal is still a consistent whole;
           fail_at 2/3: new header present -> no slot may validate, never the stale seq-4 slot */
        if (fail_at >= 2) ASSERT_EQ_INT(rs, DM_E_JOURNAL_CORRUPT);
        mem_io_free(&m);
    }
}

void dm_run_journal(void)
{
    DM_RUN(test_journal_roundtrip);
    DM_RUN(test_journal_slots_alternate);
    DM_RUN(test_journal_torn_slot_write);
    DM_RUN(test_journal_corrupt_header);
    DM_RUN(test_journal_rejects_bad_args);
    DM_RUN(test_journal_boot_sectors_roundtrip);
    DM_RUN(test_journal_rejects_v1);
    DM_RUN(test_journal_boot_restored_flag);
    DM_RUN(test_journal_200k_extents);
    DM_RUN(test_journal_stale_slot_from_previous_journal);
    DM_RUN(test_journal_stale_slot_not_bound_to_other_header);
    DM_RUN(test_journal_crash_between_header_and_slot);
}
