#include "dm_test.h"
#include "mem_io.h"
#include <diskmove/dm_core.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define KIB 1024ull
#define MIB (1024ull * KIB)
#define MAX_EXT 64

static uint64_t rng_next(uint64_t *s)    /* splitmix64 */
{
    uint64_t z = (*s += 0x9E3779B97F4A7C15ull);
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
    return z ^ (z >> 31);
}

/* Journal v2: the saved boot sectors are the partition's first and last logical sector of the image. */
static void set_boot_from(dm_move_plan *p, const uint8_t *image)
{
    p->boot_first = image + p->old_start;
    p->boot_backup = image + p->old_start + p->length - p->sector_size;
}

static int is_zero(const uint8_t *p, uint64_t len)
{
    for (uint64_t i = 0; i < len; i++) if (p[i]) return 0;
    return 1;
}

/* After a finished move: the old first sector is zero (invalidated), and so is the old backup sector unless the
   destination has overwritten it with moved data. */
static void assert_old_boot_invalidated(const uint8_t *image, const dm_move_plan *p)
{
    if (!is_zero(image + p->old_start, p->sector_size)) DM_FAIL("old first sector not invalidated");
    uint64_t old_backup = p->old_start + p->length - p->sector_size;
    if (old_backup < p->old_start + p->shift && !is_zero(image + old_backup, p->sector_size))
        DM_FAIL("old backup boot sector not invalidated");
}

/* One disk image + journal sharing one mem_fault (a single write counter spans both). */
typedef struct fx {
    dm_move_plan plan;
    dm_extent    ext[MAX_EXT];
    uint8_t     *orig;
    uint64_t     size;
    mem_fault    fault;
    mem_io       disk, jr;
    dm_io        dio, jio;
} fx;

/* Fills a fresh disk image with seeded random data and creates the journal. The fault counter is reset
   after the journal is created, so fault.writes counts only the writes of dm_run_move. */
static void fx_reset(fx *f)
{
    memcpy(f->disk.data, f->orig, (size_t)f->size);
    mem_io_free(&f->jr);
    memset(&f->fault, 0, sizeof f->fault);
    mem_io_init(&f->jr, &f->fault);
    f->jio = mem_io_as_dm_io(&f->jr);
    ASSERT_EQ_INT(dm_journal_create(&f->jio, &f->plan, 1, NULL, 0), DM_OK);
    f->fault.writes = 0;
}

/* Image = [old_start = 1 MiB][partition len][shift][1 MiB], all random; next random extents
   (unaligned offsets/lengths, up to len/8 each). */
static void make_image(fx *f, uint32_t sector, uint64_t len, uint64_t shift, uint32_t next, uint64_t seed)
{
    memset(f, 0, sizeof *f);
    uint64_t rs = seed;
    f->plan.sector_size = sector;
    f->plan.old_start = 1 * MIB;
    f->plan.length = len;
    f->plan.shift = shift;
    memset(f->plan.disk_guid, 0x11, 16);
    memset(f->plan.partition_guid, 0x22, 16);
    for (uint32_t i = 0; i < next && i < MAX_EXT; i++) {
        uint64_t off = rng_next(&rs) % len;
        uint64_t max = len - off < len / 8 ? len - off : len / 8;
        f->ext[i].offset = off;
        f->ext[i].length = 1 + rng_next(&rs) % max;
    }
    f->plan.extent_count = next;
    f->plan.extents = f->ext;

    f->size = f->plan.old_start + len + shift + 1 * MIB;
    f->orig = (uint8_t *)malloc((size_t)f->size);
    uint8_t *d = (uint8_t *)malloc((size_t)f->size);
    ASSERT_TRUE(f->orig && d);
    for (uint64_t i = 0; i < f->size; i += 8) {
        uint64_t v = rng_next(&rs);
        memcpy(f->orig + i, &v, 8);
    }
    set_boot_from(&f->plan, f->orig);   /* what the caller reads after lock + dismount: the real sectors */
    mem_io_init(&f->disk, &f->fault);
    f->disk.data = d; f->disk.size = f->disk.cap = f->size;
    f->dio = mem_io_as_dm_io(&f->disk);
    mem_io_init(&f->jr, &f->fault);
    fx_reset(f);
}

static void fx_free(fx *f)
{
    mem_io_free(&f->disk);
    mem_io_free(&f->jr);
    free(f->orig);
}

static int region_moved(const uint8_t *image, const dm_move_plan *p, const uint8_t *orig, uint64_t off, uint64_t len)
{
    return memcmp(image + p->old_start + p->shift + off, orig + p->old_start + off, (size_t)len) == 0;
}

/* Every byte of the used regions (extents + 1 MiB head/tail) sits at +shift. */
static void assert_moved(const uint8_t *image, const dm_move_plan *p, const uint8_t *orig)
{
    uint64_t ht = p->length < MIB ? p->length : MIB;
    if (!region_moved(image, p, orig, 0, ht)) DM_FAIL("head not moved");
    if (!region_moved(image, p, orig, p->length - ht, ht)) DM_FAIL("tail not moved");
    for (uint32_t i = 0; i < p->extent_count; i++)
        if (!region_moved(image, p, orig, p->extents[i].offset, p->extents[i].length))
            DM_FAIL("extent %u (off %llu len %llu) not moved", i,
                    (unsigned long long)p->extents[i].offset, (unsigned long long)p->extents[i].length);
}

/* Bytes before the partition and after the destination are never touched. */
static void assert_outside_untouched(const fx *f)
{
    ASSERT_TRUE(memcmp(f->disk.data, f->orig, (size_t)f->plan.old_start) == 0);
    uint64_t end = f->plan.old_start + f->plan.shift + f->plan.length;
    ASSERT_TRUE(memcmp(f->disk.data + end, f->orig + end, (size_t)(f->size - end)) == 0);
}

static uint64_t journal_chunks_done(const dm_io *j)
{
    dm_journal_info info; uint32_t alen;
    ASSERT_EQ_INT(dm_journal_read(j, &info, NULL, 0, &alen), DM_OK);
    return info.chunks_done;
}

static uint64_t journal_chunk_count(const dm_io *j)
{
    dm_journal_info info; uint32_t alen;
    ASSERT_EQ_INT(dm_journal_read(j, &info, NULL, 0, &alen), DM_OK);
    return info.chunk_count[0];
}

static void run_and_check(uint32_t sector, uint64_t len, uint64_t shift, uint32_t next, uint64_t seed)
{
    fx f;
    make_image(&f, sector, len, shift, next, seed);
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, NULL, NULL), DM_OK);
    assert_moved(f.disk.data, &f.plan, f.orig);
    assert_outside_untouched(&f);
    assert_old_boot_invalidated(f.disk.data, &f.plan);
    ASSERT_EQ_U64(journal_chunks_done(&f.jio), journal_chunk_count(&f.jio));
    fx_free(&f);
}

static void move_basic(void) { run_and_check(512, 64 * MIB, 8 * MIB, 20, 1); }
static void move_shift_smaller_than_chunk(void) { run_and_check(512, 64 * MIB, 1 * MIB, 20, 2); }
static void move_shift_larger_than_partition(void) { run_and_check(512, 64 * MIB, 128 * MIB, 20, 3); }
static void move_4kn_sectors(void) { run_and_check(4096, 64 * MIB, 8 * MIB, 20, 4); }

/* Crash at write N (disk or journal, shared counter), then resume on a healthy io -> fully moved. */
static void crash_and_resume(fx *f, uint64_t n)
{
    fx_reset(f);
    f->fault.fail_after_writes = n;
    dm_status s = dm_run_move(&f->dio, &f->jio, 0, NULL, NULL);
    if (s != DM_E_IO) DM_FAIL("N=%llu: crashed run returned %d", (unsigned long long)n, (int)s);
    ASSERT_TRUE(f->fault.tripped);
    f->fault.tripped = 0;
    f->fault.fail_after_writes = 0;
    s = dm_run_move(&f->dio, &f->jio, 0, NULL, NULL);
    if (s != DM_OK) DM_FAIL("N=%llu: resume returned %d", (unsigned long long)n, (int)s);
    assert_moved(f->disk.data, &f->plan, f->orig);
    assert_outside_untouched(f);
    assert_old_boot_invalidated(f->disk.data, &f->plan);
    dm_journal_info info; uint32_t alen;
    ASSERT_EQ_INT(dm_journal_read(&f->jio, &info, NULL, 0, &alen), DM_OK);
    if (info.boot_restored != 1) DM_FAIL("N=%llu: restore not recorded", (unsigned long long)n);
}

static void move_crash_every_write(void)
{
    clock_t t0 = clock();
    fx f;
    make_image(&f, 512, 6 * MIB, 1 * MIB, 20, 5);    /* 8 MiB image */
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, NULL, NULL), DM_OK);
    uint64_t total = f.fault.writes;
    ASSERT_TRUE(total >= 4);
    for (uint64_t n = 1; n <= total; n++) crash_and_resume(&f, n);
    printf("  move_crash_every_write: %llu writes per clean run, %llu crash points, %.2f s\n",
           (unsigned long long)total, (unsigned long long)total, (double)(clock() - t0) / CLOCKS_PER_SEC);
    fx_free(&f);
}

/* As the callers use it (one extent = the whole partition, shift < length, so the old backup sector lies inside
   the destination): crash at every write, including the two invalidation writes, every chunk, every journal
   record, the two restore writes and the boot_restored record; then resume. */
static void move_crash_every_write_whole_partition(void)
{
    for (uint32_t sector = 512; sector <= 4096; sector *= 8) {
        fx f;
        make_image(&f, sector, 6 * MIB, 1 * MIB, 0, 30 + sector);
        f.ext[0].offset = 0; f.ext[0].length = f.plan.length;
        f.plan.extent_count = 1;
        fx_reset(&f);
        ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, NULL, NULL), DM_OK);
        uint64_t total = f.fault.writes;
        for (uint64_t n = 1; n <= total; n++) {
            crash_and_resume(&f, n);
            uint64_t dst = f.plan.old_start + f.plan.shift;
            if (memcmp(f.disk.data + dst, f.orig + f.plan.old_start, (size_t)f.plan.length) != 0)
                DM_FAIL("sector %u, N=%llu: destination differs from the original partition", sector, (unsigned long long)n);
        }
        printf("  move_crash_every_write_whole_partition (%u): %llu crash points\n", sector, (unsigned long long)total);
        fx_free(&f);
    }
}

static void move_crash_random_large(void)
{
    clock_t t0 = clock();
    fx f;
    make_image(&f, 512, 222 * MIB, 32 * MIB, 20, 6);   /* 1 + 222 + 32 + 1 = 256 MiB image */
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, NULL, NULL), DM_OK);
    uint64_t total = f.fault.writes;
    ASSERT_TRUE(total >= 4);
    uint64_t rs = 0xC0FFEEull;
    for (int i = 0; i < 200; i++) crash_and_resume(&f, 1 + rng_next(&rs) % total);
    printf("  move_crash_random_large: %llu writes per clean run, 200 crash points, %.2f s\n",
           (unsigned long long)total, (double)(clock() - t0) / CLOCKS_PER_SEC);
    fx_free(&f);
}

/* dm_io wrapper: every read of exactly [off, off+len) comes back with one byte flipped. */
typedef struct flip_io { dm_io inner; uint64_t off; uint32_t len; int hits; } flip_io;
static dm_status flip_read(void *ctx, uint64_t off, void *buf, uint32_t len)
{
    flip_io *f = (flip_io *)ctx;
    dm_status s = f->inner.read(f->inner.ctx, off, buf, len);
    if (s == DM_OK && off == f->off && len == f->len) { ((uint8_t *)buf)[len / 2] ^= 0x5A; f->hits++; }
    return s;
}
static dm_status flip_write(void *ctx, uint64_t off, const void *buf, uint32_t len)
{
    flip_io *f = (flip_io *)ctx;
    return f->inner.write(f->inner.ctx, off, buf, len);
}
static dm_status flip_flush(void *ctx)
{
    flip_io *f = (flip_io *)ctx;
    return f->inner.flush(f->inner.ctx);
}

static void move_verify_failure(void)
{
    fx f;
    make_image(&f, 512, 16 * MIB, 1 * MIB, 4, 7);
    dm_move_plan sp; dm_extent *ch = NULL; uint32_t n = 0;
    ASSERT_EQ_INT(dm_journal_load_move(&f.jio, 0, &sp, &ch, &n), DM_OK);
    ASSERT_TRUE(n > 3);
    const uint32_t k = 2;
    flip_io fl;
    fl.inner = f.dio;
    fl.off = f.plan.old_start + ch[k].offset + f.plan.shift;
    fl.len = (uint32_t)ch[k].length;
    fl.hits = 0;
    dm_io bad = { &fl, flip_read, flip_write, flip_flush };

    ASSERT_EQ_INT(dm_run_move(&bad, &f.jio, 0, NULL, NULL), DM_E_VERIFY);
    ASSERT_EQ_INT(fl.hits, 4);                               /* first read-back + 3 retries */
    ASSERT_EQ_U64(journal_chunks_done(&f.jio), k);
    uint64_t src = f.plan.old_start + ch[k].offset;
    ASSERT_TRUE(memcmp(f.disk.data + src, f.orig + src, (size_t)ch[k].length) == 0);

    /* the fault goes away -> resume completes */
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, NULL, NULL), DM_OK);
    assert_moved(f.disk.data, &f.plan, f.orig);
    dm_free(ch);
    fx_free(&f);
}

typedef struct prog { int calls, stop_at; uint64_t first_done, last_done, last_total; } prog;
static int prog_cb(void *user, uint64_t done, uint64_t total)
{
    prog *p = (prog *)user;
    if (p->calls == 0) p->first_done = done;
    p->calls++;
    p->last_done = done;
    p->last_total = total;
    return p->calls == p->stop_at;
}

static void move_stop_between_chunks(void)
{
    fx f;
    make_image(&f, 512, 16 * MIB, 1 * MIB, 6, 8);
    dm_move_plan sp; dm_extent *ch = NULL; uint32_t n = 0;
    ASSERT_EQ_INT(dm_journal_load_move(&f.jio, 0, &sp, &ch, &n), DM_OK);
    ASSERT_TRUE(n > 4);
    uint64_t total = 0, three = 0;
    for (uint32_t i = 0; i < n; i++) { total += ch[i].length; if (i < 3) three += ch[i].length; }

    prog p = { 0, 4, 0, 0, 0 };                              /* call 1 = before first chunk, 4 = after chunk 3 */
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, prog_cb, &p), DM_E_STOPPED);
    ASSERT_EQ_U64(journal_chunks_done(&f.jio), 3);
    ASSERT_EQ_U64(p.last_done, three);
    ASSERT_EQ_U64(p.last_total, total);

    prog q = { 0, 0, 0, 0, 0 };
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, prog_cb, &q), DM_OK);
    ASSERT_EQ_U64(q.first_done, three);
    ASSERT_EQ_U64(q.last_done, total);
    ASSERT_EQ_INT(q.calls, 1 + (int)(n - 3));
    assert_moved(f.disk.data, &f.plan, f.orig);
    assert_outside_untouched(&f);
    ASSERT_EQ_U64(journal_chunks_done(&f.jio), n);

    /* already complete -> DM_OK, no callback, no write */
    prog r = { 0, 0, 0, 0, 0 };
    f.fault.writes = 0;
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, prog_cb, &r), DM_OK);
    ASSERT_EQ_INT(r.calls, 0);
    ASSERT_EQ_U64(f.fault.writes, 0);
    dm_free(ch);
    fx_free(&f);
}

static void move_stop_before_first_chunk(void)
{
    fx f;
    make_image(&f, 512, 16 * MIB, 1 * MIB, 6, 9);
    prog p = { 0, 1, 0, 0, 0 };
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, prog_cb, &p), DM_E_STOPPED);
    ASSERT_EQ_INT(p.calls, 1);
    ASSERT_EQ_U64(p.first_done, 0);
    ASSERT_EQ_U64(f.fault.writes, 0);
    ASSERT_TRUE(memcmp(f.disk.data, f.orig, (size_t)f.size) == 0);
    ASSERT_EQ_U64(journal_chunks_done(&f.jio), 0);
    fx_free(&f);
}

/* Two moves in one journal: run in order, finished moves are no-ops, out of order is a mismatch. */
static void move_two_moves_in_order(void)
{
    fx f;
    make_image(&f, 512, 48 * MIB, 8 * MIB, 0, 10);         /* 58 MiB random image; plans set below */
    dm_extent e0[2] = { { 100, 3 * MIB }, { 5 * MIB, 77 } };
    dm_extent e1[1] = { { 2 * MIB + 5, 6 * MIB } };
    dm_move_plan plans[2] = { f.plan, f.plan };
    plans[0].old_start = 40 * MIB; plans[0].length = 8 * MIB;  plans[0].shift = 4 * MIB;
    plans[0].extents = e0; plans[0].extent_count = 2;
    plans[1].old_start = 1 * MIB;  plans[1].length = 16 * MIB; plans[1].shift = 4 * MIB;
    plans[1].extents = e1; plans[1].extent_count = 1;
    set_boot_from(&plans[0], f.orig);
    set_boot_from(&plans[1], f.orig);
    mem_io_free(&f.jr);
    mem_io_init(&f.jr, &f.fault);
    f.jio = mem_io_as_dm_io(&f.jr);
    ASSERT_EQ_INT(dm_journal_create(&f.jio, plans, 2, "app", 3), DM_OK);   /* app blob must not get in the way */

    f.fault.writes = 0;
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 1, NULL, NULL), DM_E_JOURNAL_MISMATCH);
    ASSERT_EQ_U64(f.fault.writes, 0);
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, NULL, NULL), DM_OK);
    assert_moved(f.disk.data, &plans[0], f.orig);
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 1, NULL, NULL), DM_OK);
    assert_moved(f.disk.data, &plans[1], f.orig);

    dm_journal_info info; uint32_t alen; char app[8];
    ASSERT_EQ_INT(dm_journal_read(&f.jio, &info, app, sizeof app, &alen), DM_OK);
    ASSERT_EQ_U32(info.move_index, 1);
    ASSERT_EQ_U64(info.chunks_done, info.chunk_count[1]);

    f.fault.writes = 0;
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, NULL, NULL), DM_OK);   /* finished earlier move: no I/O */
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 1, NULL, NULL), DM_OK);
    ASSERT_EQ_U64(f.fault.writes, 0);
    fx_free(&f);
}

/* Two partitions on one 12 MiB image, moved like the real caller does (farthest partition first):
   move 0 = "data" [3 MiB, 9 MiB) -> +1 MiB, then move 1 = "recovery" [1 MiB, 3 MiB) -> +1 MiB into the space
   move 0 vacated. Both journal moves live in one journal, so the slot's move_index changes mid-sequence. */
typedef struct fx2 { fx f; dm_move_plan plans[2]; dm_extent e0[3], e1[2]; } fx2;

static void fx2_reset(fx2 *t)
{
    fx *f = &t->f;
    memcpy(f->disk.data, f->orig, (size_t)f->size);
    mem_io_free(&f->jr);
    memset(&f->fault, 0, sizeof f->fault);
    mem_io_init(&f->jr, &f->fault);
    f->jio = mem_io_as_dm_io(&f->jr);
    ASSERT_EQ_INT(dm_journal_create(&f->jio, t->plans, 2, "blob", 4), DM_OK);
    f->fault.writes = 0;
}

static void fx2_make(fx2 *t)
{
    memset(t, 0, sizeof *t);
    make_image(&t->f, 512, 9 * MIB, 1 * MIB, 0, 12);       /* 1 + 9 + 1 + 1 = 12 MiB random image */
    dm_extent e0[3] = { { 1 * MIB + 100, 700 * KIB }, { 2 * MIB + 3, 1 * MIB + 5 }, { 4 * MIB - 1, 2 } };
    dm_extent e1[2] = { { 4096 + 7, 300 * KIB }, { 1 * MIB - 9, 18 } };
    memcpy(t->e0, e0, sizeof e0);
    memcpy(t->e1, e1, sizeof e1);
    t->plans[0] = t->f.plan;
    t->plans[0].old_start = 3 * MIB; t->plans[0].length = 6 * MIB; t->plans[0].shift = 1 * MIB;
    t->plans[0].extents = t->e0;     t->plans[0].extent_count = 3;
    t->plans[1] = t->f.plan;
    t->plans[1].old_start = 1 * MIB; t->plans[1].length = 2 * MIB; t->plans[1].shift = 1 * MIB;
    t->plans[1].extents = t->e1;     t->plans[1].extent_count = 2;
    memset(t->plans[1].partition_guid, 0x33, 16);
    set_boot_from(&t->plans[0], t->f.orig);
    set_boot_from(&t->plans[1], t->f.orig);
    fx2_reset(t);
}

/* The caller's real sequence. */
static dm_status run_both(fx2 *t)
{
    dm_status s = dm_run_move(&t->f.dio, &t->f.jio, 0, NULL, NULL);
    return s != DM_OK ? s : dm_run_move(&t->f.dio, &t->f.jio, 1, NULL, NULL);
}

static void assert_both_moved(fx2 *t)
{
    assert_moved(t->f.disk.data, &t->plans[0], t->f.orig);
    assert_moved(t->f.disk.data, &t->plans[1], t->f.orig);
    /* nothing before move 1's source or after move 0's destination is touched */
    ASSERT_TRUE(memcmp(t->f.disk.data, t->f.orig, (size_t)(1 * MIB)) == 0);
    ASSERT_TRUE(memcmp(t->f.disk.data + 10 * MIB, t->f.orig + 10 * MIB, (size_t)(2 * MIB)) == 0);
    dm_journal_info info; uint32_t alen; char app[8];
    ASSERT_EQ_INT(dm_journal_read(&t->f.jio, &info, app, sizeof app, &alen), DM_OK);
    ASSERT_EQ_U32(info.move_index, 1);
    ASSERT_EQ_U64(info.chunks_done, info.chunk_count[1]);
}

static uint64_t two_moves_clean_writes(fx2 *t, uint64_t *move0_writes)
{
    fx2_reset(t);
    ASSERT_EQ_INT(dm_run_move(&t->f.dio, &t->f.jio, 0, NULL, NULL), DM_OK);
    *move0_writes = t->f.fault.writes;
    ASSERT_EQ_INT(dm_run_move(&t->f.dio, &t->f.jio, 1, NULL, NULL), DM_OK);
    assert_both_moved(t);
    return t->f.fault.writes;
}

static void move_crash_every_write_two_moves(void)
{
    clock_t t0 = clock();
    fx2 *t = (fx2 *)malloc(sizeof *t);
    ASSERT_TRUE(t != NULL);
    fx2_make(t);
    uint64_t w0 = 0, total = two_moves_clean_writes(t, &w0);
    ASSERT_TRUE(w0 >= 4 && total > w0 + 2);
    for (uint64_t n = 1; n <= total; n++) {
        fx2_reset(t);
        t->f.fault.fail_after_writes = n;
        dm_status s = run_both(t);
        if (s != DM_E_IO) DM_FAIL("N=%llu: crashed sequence returned %d", (unsigned long long)n, (int)s);
        t->f.fault.tripped = 0;
        t->f.fault.fail_after_writes = 0;
        s = run_both(t);
        if (s != DM_OK) DM_FAIL("N=%llu: resumed sequence returned %d", (unsigned long long)n, (int)s);
        assert_both_moved(t);
    }
    printf("  move_crash_every_write_two_moves: %llu writes per clean run (%llu in move 0), %.2f s\n",
           (unsigned long long)total, (unsigned long long)w0, (double)(clock() - t0) / CLOCKS_PER_SEC);
    fx_free(&t->f);
    free(t);
}

/* Crash at N, crash again during the resume at M writes later, then a clean resume. */
static void move_crash_twice(void)
{
    fx2 *t = (fx2 *)malloc(sizeof *t);
    ASSERT_TRUE(t != NULL);
    fx2_make(t);
    uint64_t w0 = 0, total = two_moves_clean_writes(t, &w0);
    /* (N, M): within move 0, across the transition (last write of move 0 / first of move 1), within move 1 */
    const uint64_t pairs[][2] = {
        { 1, 1 }, { 2, 1 }, { 3, 2 }, { w0 / 2, 1 }, { w0 / 2, 3 }, { w0 - 1, 1 }, { w0 - 1, 2 },
        { w0, 1 }, { w0, 2 }, { w0 + 1, 1 }, { w0 + 2, 2 }, { total - 2, 1 }, { total - 1, 1 }, { total, 1 },
    };
    int second_crashes = 0;
    for (size_t i = 0; i < sizeof pairs / sizeof pairs[0]; i++) {
        uint64_t n = pairs[i][0], m = pairs[i][1];
        fx2_reset(t);
        t->f.fault.fail_after_writes = n;
        ASSERT_EQ_INT(run_both(t), DM_E_IO);
        t->f.fault.tripped = 0;
        t->f.fault.fail_after_writes = t->f.fault.writes + m;    /* M writes after those already done */
        dm_status s = run_both(t);
        if (s == DM_E_IO) second_crashes++;
        else if (s != DM_OK) DM_FAIL("(N=%llu, M=%llu): second run returned %d", (unsigned long long)n,
                                     (unsigned long long)m, (int)s);
        t->f.fault.tripped = 0;
        t->f.fault.fail_after_writes = 0;
        s = run_both(t);
        if (s != DM_OK) DM_FAIL("(N=%llu, M=%llu): clean resume returned %d", (unsigned long long)n,
                                (unsigned long long)m, (int)s);
        assert_both_moved(t);
    }
    ASSERT_TRUE(second_crashes >= 10);    /* the second fault really hit in (almost) every pair */
    fx_free(&t->f);
    free(t);
}

static void move_rejects_bad_args(void)
{
    fx f;
    make_image(&f, 512, 4 * MIB, 1 * MIB, 0, 11);
    ASSERT_EQ_INT(dm_run_move(NULL, &f.jio, 0, NULL, NULL), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dm_run_move(&f.dio, NULL, 0, NULL, NULL), DM_E_INVALID_ARG);
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 1, NULL, NULL), DM_E_INVALID_ARG);   /* only 1 move */
    dm_io noflush = f.dio; noflush.flush = NULL;
    ASSERT_EQ_INT(dm_run_move(&noflush, &f.jio, 0, NULL, NULL), DM_E_INVALID_ARG);
    mem_io empty; mem_io_init(&empty, NULL);
    dm_io ej = mem_io_as_dm_io(&empty);
    ASSERT_EQ_INT(dm_run_move(&f.dio, &ej, 0, NULL, NULL), DM_E_JOURNAL_CORRUPT);
    ASSERT_EQ_U64(f.fault.writes, 0);
    fx_free(&f);
}


/* ---- Journal v2: old boot sectors invalidated before chunk 0, saved ones restored after the last chunk ---- */

/* dm_io wrapper that records every write (offset, length, all-zero?) into a fixed log. */
#define WLOG_MAX 4096
typedef struct wlog { dm_io inner; uint32_t n; uint64_t off[WLOG_MAX]; uint32_t len[WLOG_MAX]; int zero[WLOG_MAX]; } wlog;
static dm_status wlog_read(void *ctx, uint64_t off, void *buf, uint32_t len)
{
    wlog *w = (wlog *)ctx;
    return w->inner.read(w->inner.ctx, off, buf, len);
}
static dm_status wlog_write(void *ctx, uint64_t off, const void *buf, uint32_t len)
{
    wlog *w = (wlog *)ctx;
    if (w->n < WLOG_MAX) { w->off[w->n] = off; w->len[w->n] = len; w->zero[w->n] = is_zero((const uint8_t *)buf, len); w->n++; }
    return w->inner.write(w->inner.ctx, off, buf, len);
}
static dm_status wlog_flush(void *ctx)
{
    wlog *w = (wlog *)ctx;
    return w->inner.flush(w->inner.ctx);
}
static dm_io wlog_io(wlog *w, dm_io inner)
{
    memset(w, 0, sizeof *w);
    w->inner = inner;
    dm_io io = { w, wlog_read, wlog_write, wlog_flush };
    return io;
}

/* Saved sectors differ from what is on the disk: the destination gets exactly the saved ones, the old ones are
   zeroed, every other byte is moved. shift > length, so the old backup sector stays outside the destination. */
static void move_restores_saved_boot_sectors(void)
{
    for (uint32_t sector = 512; sector <= 4096; sector *= 8) {
        fx f;
        make_image(&f, sector, 16 * MIB, 32 * MIB, 0, 20 + sector);
        f.ext[0].offset = 0; f.ext[0].length = f.plan.length;      /* whole partition used, like the callers */
        f.plan.extent_count = 1;
        static uint8_t first[4096], backup[4096];
        for (uint32_t i = 0; i < sector; i++) { first[i] = (uint8_t)(0xEB ^ i); backup[i] = (uint8_t)(0x52 + 7 * i); }
        f.plan.boot_first = first;
        f.plan.boot_backup = backup;
        fx_reset(&f);
        ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, NULL, NULL), DM_OK);

        const dm_move_plan *p = &f.plan;
        uint64_t dst = p->old_start + p->shift, len = p->length;
        ASSERT_TRUE(memcmp(f.disk.data + dst, first, sector) == 0);
        ASSERT_TRUE(memcmp(f.disk.data + dst + len - sector, backup, sector) == 0);
        ASSERT_TRUE(is_zero(f.disk.data + p->old_start, sector));
        ASSERT_TRUE(is_zero(f.disk.data + p->old_start + len - sector, sector));
        /* everything between the two boot sectors moved (whole range: head/tail regions included) */
        ASSERT_TRUE(memcmp(f.disk.data + dst + sector, f.orig + p->old_start + sector, (size_t)(len - 2 * sector)) == 0);
        assert_outside_untouched(&f);
        dm_journal_info info; uint32_t alen;
        ASSERT_EQ_INT(dm_journal_read(&f.jio, &info, NULL, 0, &alen), DM_OK);
        ASSERT_EQ_U32(info.boot_restored, 1);
        fx_free(&f);
    }
}

/* A fresh run: the first two disk writes zero the old first and backup sectors (then a flush), before chunk 0;
   the last two write the saved sectors at the new start and the new end. */
static void move_invalidates_before_first_chunk(void)
{
    fx f;
    make_image(&f, 512, 16 * MIB, 1 * MIB, 4, 21);
    wlog *w = (wlog *)malloc(sizeof *w);
    ASSERT_TRUE(w != NULL);
    if (!w) return;
    dm_io io = wlog_io(w, f.dio);
    ASSERT_EQ_INT(dm_run_move(&io, &f.jio, 0, NULL, NULL), DM_OK);
    ASSERT_TRUE(w->n >= 4);
    ASSERT_EQ_U64(w->off[0], f.plan.old_start);
    ASSERT_EQ_U32(w->len[0], 512);
    ASSERT_TRUE(w->zero[0]);
    ASSERT_EQ_U64(w->off[1], f.plan.old_start + f.plan.length - 512);
    ASSERT_EQ_U32(w->len[1], 512);
    ASSERT_TRUE(w->zero[1]);
    ASSERT_TRUE(w->len[2] > 512);                                    /* then the first chunk */
    uint64_t dst = f.plan.old_start + f.plan.shift;
    ASSERT_EQ_U64(w->off[w->n - 2], dst);
    ASSERT_EQ_U32(w->len[w->n - 2], 512);
    ASSERT_EQ_U64(w->off[w->n - 1], dst + f.plan.length - 512);
    ASSERT_EQ_U32(w->len[w->n - 1], 512);
    assert_moved(f.disk.data, &f.plan, f.orig);
    free(w);
    fx_free(&f);
}

/* A run stopped before the first chunk writes nothing (not even the invalidation). */
static void move_stop_before_first_chunk_no_invalidation(void)
{
    fx f;
    make_image(&f, 512, 16 * MIB, 1 * MIB, 4, 22);
    prog p = { 0, 1, 0, 0, 0 };
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, prog_cb, &p), DM_E_STOPPED);
    ASSERT_TRUE(memcmp(f.disk.data, f.orig, (size_t)f.size) == 0);
    fx_free(&f);
}

/* Resuming with chunks_done > 0 never invalidates again: the old backup sector position now lies inside the
   destination (shift < length) and holds moved data, which must survive. */
static void move_resume_never_invalidates(void)
{
    fx f;
    make_image(&f, 512, 16 * MIB, 1 * MIB, 0, 23);
    f.ext[0].offset = 0; f.ext[0].length = f.plan.length;          /* whole partition used, like the callers */
    f.plan.extent_count = 1;
    fx_reset(&f);
    uint64_t old_backup = f.plan.old_start + f.plan.length - 512;
    ASSERT_TRUE(old_backup >= f.plan.old_start + f.plan.shift);      /* inside the destination */
    /* stop right after the chunk whose destination covers the old backup sector position */
    dm_move_plan sp; dm_extent *ch = NULL; uint32_t n = 0, c = 0;
    ASSERT_EQ_INT(dm_journal_load_move(&f.jio, 0, &sp, &ch, &n), DM_OK);
    while (c < n && !(ch[c].offset + f.plan.shift <= f.plan.length - 512 &&
                      f.plan.length - 512 < ch[c].offset + ch[c].length + f.plan.shift)) c++;
    ASSERT_TRUE(c + 1 < n);
    dm_free(ch);
    prog p = { 0, (int)c + 2, 0, 0, 0 };
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, prog_cb, &p), DM_E_STOPPED);
    ASSERT_EQ_U64(journal_chunks_done(&f.jio), c + 1);
    uint8_t moved[512];
    memcpy(moved, f.disk.data + old_backup, 512);                     /* the copied data now at that position */
    ASSERT_TRUE(memcmp(moved, f.orig + old_backup - f.plan.shift, 512) == 0);

    wlog *w = (wlog *)malloc(sizeof *w);
    ASSERT_TRUE(w != NULL);
    if (!w) return;
    dm_io io = wlog_io(w, f.dio);
    ASSERT_EQ_INT(dm_run_move(&io, &f.jio, 0, NULL, NULL), DM_OK);
    for (uint32_t i = 0; i < w->n; i++) {
        if (w->len[i] == 512 && (w->off[i] == f.plan.old_start || w->off[i] == old_backup))
            DM_FAIL("write %u: old boot sector rewritten on resume (off %llu)", i, (unsigned long long)w->off[i]);
    }
    ASSERT_TRUE(memcmp(f.disk.data + old_backup, moved, 512) == 0);  /* not zeroed */
    assert_moved(f.disk.data, &f.plan, f.orig);
    free(w);
    fx_free(&f);
}

/* Re-entry on a complete move: the restore is re-applied while the journal does not record it, and never again
   once it does (so it cannot undo the caller's later NTFS HiddenSectors patch). */
static void move_complete_reentry_restore(void)
{
    fx f;
    make_image(&f, 512, 16 * MIB, 1 * MIB, 4, 24);
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, NULL, NULL), DM_OK);
    uint64_t dst = f.plan.old_start + f.plan.shift;
    uint64_t n = journal_chunk_count(&f.jio);

    /* complete and recorded: no I/O even though the sector changed meanwhile (the caller's patch) */
    f.disk.data[dst + 0x1C] ^= 0xFF;
    f.fault.writes = 0;
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, NULL, NULL), DM_OK);
    ASSERT_EQ_U64(f.fault.writes, 0);
    f.disk.data[dst + 0x1C] ^= 0xFF;

    /* crash between the last progress record and the restore: the journal says complete, not restored */
    memset(f.disk.data + dst, 0, 512);
    memset(f.disk.data + dst + f.plan.length - 512, 0, 512);
    ASSERT_EQ_INT(dm_journal_progress(&f.jio, 0, n, 0), DM_OK);
    ASSERT_EQ_INT(dm_run_move(&f.dio, &f.jio, 0, NULL, NULL), DM_OK);
    assert_moved(f.disk.data, &f.plan, f.orig);
    dm_journal_info info; uint32_t alen;
    ASSERT_EQ_INT(dm_journal_read(&f.jio, &info, NULL, 0, &alen), DM_OK);
    ASSERT_EQ_U32(info.boot_restored, 1);
    fx_free(&f);
}

/* Starting move 1 while move 0 is complete but its restore was not recorded: move 0's restore runs first. */
static void move_next_move_restores_previous(void)
{
    fx2 *t = (fx2 *)malloc(sizeof *t);
    ASSERT_TRUE(t != NULL);
    if (!t) return;
    fx2_make(t);
    ASSERT_EQ_INT(dm_run_move(&t->f.dio, &t->f.jio, 0, NULL, NULL), DM_OK);
    const dm_move_plan *p0 = &t->plans[0];
    uint64_t dst0 = p0->old_start + p0->shift;
    memset(t->f.disk.data + dst0, 0, 512);
    memset(t->f.disk.data + dst0 + p0->length - 512, 0, 512);
    dm_journal_info info; uint32_t alen; char app[8];
    ASSERT_EQ_INT(dm_journal_read(&t->f.jio, &info, app, sizeof app, &alen), DM_OK);
    ASSERT_EQ_INT(dm_journal_progress(&t->f.jio, 0, info.chunk_count[0], 0), DM_OK);   /* "not restored" */
    ASSERT_EQ_INT(dm_run_move(&t->f.dio, &t->f.jio, 1, NULL, NULL), DM_OK);
    assert_both_moved(t);
    fx_free(&t->f);
    free(t);
}

/* Suite name "mover" (not "move"): the harness entry point dm_run_<suite> would otherwise collide with dm_run_move. */
void dm_run_mover(void)
{
    DM_RUN(move_basic);
    DM_RUN(move_shift_smaller_than_chunk);
    DM_RUN(move_shift_larger_than_partition);
    DM_RUN(move_4kn_sectors);
    DM_RUN(move_crash_every_write);
    DM_RUN(move_crash_every_write_whole_partition);
    DM_RUN(move_crash_random_large);
    DM_RUN(move_crash_every_write_two_moves);
    DM_RUN(move_crash_twice);
    DM_RUN(move_verify_failure);
    DM_RUN(move_stop_between_chunks);
    DM_RUN(move_stop_before_first_chunk);
    DM_RUN(move_two_moves_in_order);
    DM_RUN(move_rejects_bad_args);
    DM_RUN(move_restores_saved_boot_sectors);
    DM_RUN(move_invalidates_before_first_chunk);
    DM_RUN(move_stop_before_first_chunk_no_invalidation);
    DM_RUN(move_resume_never_invalidates);
    DM_RUN(move_complete_reentry_restore);
    DM_RUN(move_next_move_restores_previous);
}
