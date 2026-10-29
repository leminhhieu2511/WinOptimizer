/*
 * Backward chunk mover. Chunks are stored in strictly descending offset order and each is no longer than
 * shift, so copying chunk i to +shift only overwrites bytes whose chunks are already recorded as done
 * (or free space): the source of the first unrecorded chunk is always intact and re-running it after a
 * crash is safe. Per chunk: read source, write destination, flush, read back and compare (re-write up to
 * VERIFY_RETRIES more times), then record progress in the journal.
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

static dm_status copy_chunk(const dm_io *d, uint64_t src, uint64_t dst, uint32_t len, uint8_t *buf, uint8_t *chk)
{
    dm_status s = d->read(d->ctx, src, buf, len);
    if (s != DM_OK) return s;
    for (int attempt = 0; attempt <= VERIFY_RETRIES; attempt++) {
        s = d->write(d->ctx, dst, buf, len);
        if (s == DM_OK) s = d->flush(d->ctx);
        if (s == DM_OK) s = d->read(d->ctx, dst, chk, len);
        if (s != DM_OK) return s;
        if (memcmp(buf, chk, len) == 0) return DM_OK;
    }
    return DM_E_VERIFY;
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
    if (move_index < info.move_index) return DM_OK;                    /* finished earlier */
    if (move_index > info.move_index) {
        if (move_index != info.move_index + 1 || info.chunks_done != info.chunk_count[info.move_index])
            return DM_E_JOURNAL_MISMATCH;
        start = 0;
    } else {
        start = info.chunks_done;
    }

    dm_move_plan plan;
    dm_extent *ch = NULL;
    uint32_t n = 0;
    s = dm_journal_load_move(journal, move_index, &plan, &ch, &n);
    if (s != DM_OK) return s;
    abuf a = { NULL, NULL }, b = { NULL, NULL };
    if (n != info.chunk_count[move_index] || start > n) { s = DM_E_JOURNAL_CORRUPT; goto done; }
    if (start == n) goto done;                                         /* already complete */
    s = check_chunks(&plan, ch, n);
    if (s != DM_OK) goto done;

    uint64_t total = 0, done_bytes = 0;
    for (uint32_t i = 0; i < n; i++) {
        total += ch[i].length;
        if (i < start) done_bytes += ch[i].length;
    }
    if (!abuf_alloc(&a) || !abuf_alloc(&b)) { s = DM_E_NOMEM; goto done; }

    if (progress && progress(user, done_bytes, total)) { s = DM_E_STOPPED; goto done; }
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
done:
    free(a.raw);
    free(b.raw);
    dm_free(ch);
    return s;
}
