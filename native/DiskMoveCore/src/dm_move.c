/*
 * Backward chunk mover. Chunks are stored in strictly descending offset order and each is no longer than
 * shift, so copying chunk i to +shift only overwrites bytes whose chunks are already recorded as done
 * (or free space): the source of the first unrecorded chunk is always intact and re-running it after a
 * crash is safe. Per chunk: read source, write destination, flush, read back and compare (re-write up to
 * VERIFY_RETRIES more times), then record progress in the journal.
 *
 * Boot sectors (journal v2). After a crash the half-moved partition is hidden, but Windows may still mount its
 * OLD-offset volume device (seen on re-enumeration after any layout change) and write to it. So before chunk 0 of
 * a fresh move (chunks_done == 0 only) the old first sector and the old backup boot sector are zeroed: a file
 * system can then never recognise the old view. Never with chunks_done > 0: the old backup sector may already lie
 * inside the copied destination. Because those two sectors are copied as zeros, the original ones (saved in the
 * journal header by the caller before any write) are written to the destination after the last chunk, read back,
 * and the journal records boot_restored. A complete move without that record re-applies the restore on entry.
 * Before chunk 0 the sectors on the disk are compared with the saved ones (equal, or zero from an earlier
 * invalidation): a stale copy is refused with DM_E_JOURNAL_MISMATCH before any write.
 */
#include <diskmove/dm_core.h>
#include <stdlib.h>
#include <string.h>

#define MOVE_MAX_CHUNK  (16u * 1024u * 1024u)
#define MOVE_BUF_ALIGN  4096u
#define VERIFY_RETRIES  3

/* malloc + manual alignment keeps the core free of OS calls. */
typedef struct abuf { void *raw; uint8_t *p; } abuf;

static int abuf_alloc(abuf *b)
{
    b->raw = malloc((size_t)MOVE_MAX_CHUNK + MOVE_BUF_ALIGN);
    if (!b->raw) { b->p = NULL; return 0; }
    uintptr_t a = ((uintptr_t)b->raw + (MOVE_BUF_ALIGN - 1)) & ~(uintptr_t)(MOVE_BUF_ALIGN - 1);
    b->p = (uint8_t *)a;
    return 1;
}

/* Reads the newest journal slot; tolerates any app blob size. */
static dm_status read_info(const dm_io *j, dm_journal_info *info)
{
    uint32_t app_len = 0;
    dm_status s = dm_journal_read(j, info, NULL, 0, &app_len);
    if (s != DM_E_INVALID_ARG || app_len == 0) return s;
    void *app = malloc(app_len);
    if (!app) return DM_E_NOMEM;
    s = dm_journal_read(j, info, app, app_len, &app_len);
    free(app);
    return s;
}

/* The stored chunks must be exactly what makes the backward copy safe; anything else is corruption. */
static dm_status check_chunks(const dm_move_plan *p, const dm_extent *c, uint32_t n)
{
    if (dm_plan_validate(p) != DM_OK) return DM_E_JOURNAL_CORRUPT;
    for (uint32_t i = 0; i < n; i++) {
        if (c[i].length == 0 || c[i].length > MOVE_MAX_CHUNK || c[i].length > p->shift) return DM_E_JOURNAL_CORRUPT;
        if (c[i].offset > p->length || c[i].length > p->length - c[i].offset) return DM_E_JOURNAL_CORRUPT;
        if (i > 0 && c[i].offset + c[i].length > c[i - 1].offset) return DM_E_JOURNAL_CORRUPT;
    }
    return DM_OK;
}

/* Writes buf to dst, flushes, reads back and compares (re-writes up to VERIFY_RETRIES more times). */
static dm_status write_verified(const dm_io *d, uint64_t dst, const uint8_t *buf, uint32_t len, uint8_t *chk)
{
    dm_status s;
    for (int attempt = 0; attempt <= VERIFY_RETRIES; attempt++) {
        s = d->write(d->ctx, dst, buf, len);
        if (s == DM_OK) s = d->flush(d->ctx);
        if (s == DM_OK) s = d->read(d->ctx, dst, chk, len);
        if (s != DM_OK) return s;
        if (memcmp(buf, chk, len) == 0) return DM_OK;
    }
    return DM_E_VERIFY;
}

static dm_status copy_chunk(const dm_io *d, uint64_t src, uint64_t dst, uint32_t len, uint8_t *buf, uint8_t *chk)
{
    dm_status s = d->read(d->ctx, src, buf, len);
    if (s != DM_OK) return s;
    return write_verified(d, dst, buf, len, chk);
}

/* Zeroes the old first sector and the old backup boot sector, then flushes. Only before chunk 0. */
static dm_status boot_invalidate(const dm_io *d, const dm_move_plan *p, uint8_t *buf)
{
    const uint32_t ss = p->sector_size;
    memset(buf, 0, ss);
    dm_status s = d->write(d->ctx, p->old_start, buf, ss);
    if (s == DM_OK && p->length > ss) s = d->write(d->ctx, p->old_start + p->length - ss, buf, ss);
    if (s == DM_OK) s = d->flush(d->ctx);
    return s;
}

/* Each 512-byte block of the sector on the disk equals the saved copy or is all zero (an earlier, possibly torn,
   invalidation). */
static int sector_matches_saved(const uint8_t *disk, const uint8_t *saved, uint32_t ss)
{
    for (uint32_t o = 0; o < ss; o += 512) {
        if (memcmp(disk + o, saved + o, 512) == 0) continue;
        for (uint32_t i = 0; i < 512; i++) if (disk[o + i]) return 0;
    }
    return 1;
}

/* Before chunk 0: the saved boot sectors must still be what is on the disk, else they are stale (the volume
   changed since the caller read them) and restoring them would break it: DM_E_JOURNAL_MISMATCH, nothing written. */
static dm_status boot_check_saved(const dm_io *d, const dm_io *j, uint32_t move_index, const dm_move_plan *p,
                                  uint8_t *buf, uint8_t *chk)
{
    const uint32_t ss = p->sector_size;
    if (ss > DM_MAX_SECTOR) return DM_E_JOURNAL_CORRUPT;
    uint8_t *first = buf, *backup = buf + DM_MAX_SECTOR;
    dm_status s = dm_journal_load_boot(j, move_index, first, backup);
    if (s == DM_OK) s = d->read(d->ctx, p->old_start, chk, ss);
    if (s != DM_OK) return s;
    if (!sector_matches_saved(chk, first, ss)) return DM_E_JOURNAL_MISMATCH;
    if (p->length > ss) {
        s = d->read(d->ctx, p->old_start + p->length - ss, chk, ss);
        if (s != DM_OK) return s;
        if (!sector_matches_saved(chk, backup, ss)) return DM_E_JOURNAL_MISMATCH;
    }
    return DM_OK;
}

/* Writes the saved boot sectors of move i at its destination (verified), then records boot_restored.
   Idempotent: running it again writes the same bytes. */
static dm_status boot_restore(const dm_io *d, const dm_io *j, uint32_t move_index, uint8_t *buf, uint8_t *chk)
{
    dm_move_plan p;
    dm_extent *ch = NULL;
    uint32_t n = 0;
    dm_status s = dm_journal_load_move(j, move_index, &p, &ch, &n);
    dm_free(ch);
    if (s != DM_OK) return s;
    if (dm_plan_validate(&p) != DM_OK || p.sector_size > DM_MAX_SECTOR) return DM_E_JOURNAL_CORRUPT;
    const uint32_t ss = p.sector_size;
    const uint64_t dst = p.old_start + p.shift;
    uint8_t *first = buf, *backup = buf + DM_MAX_SECTOR;
    s = dm_journal_load_boot(j, move_index, first, backup);
    if (s == DM_OK) s = write_verified(d, dst, first, ss, chk);
    if (s == DM_OK && p.length > ss) s = write_verified(d, dst + p.length - ss, backup, ss, chk);
    if (s == DM_OK) s = dm_journal_boot_restored(j, move_index);
    return s;
}

dm_status dm_run_move(const dm_io *disk, const dm_io *journal, uint32_t move_index,
                      dm_progress_cb progress, void *user)
{
    if (!disk || !disk->read || !disk->write || !disk->flush) return DM_E_INVALID_ARG;
    if (!journal || !journal->read || !journal->write) return DM_E_INVALID_ARG;

    dm_journal_info info;
    dm_status s = read_info(journal, &info);
    if (s != DM_OK) return s;
    if (move_index >= info.move_count) return DM_E_INVALID_ARG;
    if (info.move_index >= info.move_count) return DM_E_JOURNAL_CORRUPT;

    uint64_t start;
    int restore_prev = 0;
    if (move_index < info.move_index) return DM_OK;                    /* finished earlier */
    if (move_index > info.move_index) {
        if (move_index != info.move_index + 1 || info.chunks_done != info.chunk_count[info.move_index])
            return DM_E_JOURNAL_MISMATCH;
        restore_prev = !info.boot_restored;   /* crashed between move i's last chunk and its restore */
        start = 0;
    } else {
        start = info.chunks_done;
        if (start == info.chunk_count[move_index] && info.boot_restored) return DM_OK;   /* complete: no I/O */
    }

    dm_move_plan plan;
    dm_extent *ch = NULL;
    uint32_t n = 0;
    s = dm_journal_load_move(journal, move_index, &plan, &ch, &n);
    if (s != DM_OK) return s;
    abuf a = { NULL, NULL }, b = { NULL, NULL };
    if (n != info.chunk_count[move_index] || start > n) { s = DM_E_JOURNAL_CORRUPT; goto done; }
    s = check_chunks(&plan, ch, n);
    if (s != DM_OK) goto done;
    if (!abuf_alloc(&a) || !abuf_alloc(&b)) { s = DM_E_NOMEM; goto done; }

    if (restore_prev) {
        s = boot_restore(disk, journal, info.move_index, a.p, b.p);
        if (s != DM_OK) goto done;
    }
    if (start == n) {                                                  /* complete, restore not recorded */
        s = boot_restore(disk, journal, move_index, a.p, b.p);
        goto done;
    }

    uint64_t total = 0, done_bytes = 0;
    for (uint32_t i = 0; i < n; i++) {
        total += ch[i].length;
        if (i < start) done_bytes += ch[i].length;
    }

    if (start == 0) {                                                  /* stale saved copy? nothing written yet */
        s = boot_check_saved(disk, journal, move_index, &plan, a.p, b.p);
        if (s != DM_OK) goto done;
    }
    if (progress && progress(user, done_bytes, total)) { s = DM_E_STOPPED; goto done; }
    if (start == 0) {                                                  /* never once a chunk is recorded */
        s = boot_invalidate(disk, &plan, a.p);
        if (s != DM_OK) goto done;
    }
    for (uint32_t i = (uint32_t)start; i < n; i++) {
        const uint32_t len = (uint32_t)ch[i].length;
        const uint64_t src = plan.old_start + ch[i].offset;
        s = copy_chunk(disk, src, src + plan.shift, len, a.p, b.p);
        if (s != DM_OK) goto done;
        s = dm_journal_progress(journal, move_index, (uint64_t)i + 1, dm_xxh64(a.p, len, 0));
        if (s != DM_OK) goto done;
        done_bytes += len;
        if (progress && progress(user, done_bytes, total) && i + 1 < n) { s = DM_E_STOPPED; goto done; }
    }
    s = boot_restore(disk, journal, move_index, a.p, b.p);
done:
    free(a.raw);
    free(b.raw);
    dm_free(ch);
    return s;
}
