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

/* Suite name "mover" (not "move"): the harness entry point dm_run_<suite> would otherwise collide with dm_run_move. */
void dm_run_mover(void)
{
    DM_RUN(move_basic);
    DM_RUN(move_shift_smaller_than_chunk);
    DM_RUN(move_shift_larger_than_partition);
    DM_RUN(move_4kn_sectors);
    DM_RUN(move_crash_every_write);
    DM_RUN(move_crash_random_large);
    DM_RUN(move_verify_failure);
    DM_RUN(move_stop_between_chunks);
    DM_RUN(move_stop_before_first_chunk);
    DM_RUN(move_two_moves_in_order);
    DM_RUN(move_rejects_bad_args);
}
