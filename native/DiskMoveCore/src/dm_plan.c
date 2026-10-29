#include <diskmove/dm_core.h>
#include <stdlib.h>

#define DM_MIB (1024ull * 1024ull)
#define DM_MAX_CHUNK (16ull * DM_MIB)
#define DM_HEAD_TAIL (1ull * DM_MIB)

static uint64_t align_unit(const dm_move_plan *p)
{
    return p->sector_size > 4096u ? p->sector_size : 4096u;
}

dm_status dm_plan_validate(const dm_move_plan *p)
{
    if (!p) return DM_E_INVALID_ARG;
    if (p->sector_size != 512u && p->sector_size != 4096u) return DM_E_INVALID_PLAN;
    const uint64_t a = align_unit(p);
    if (p->old_start % a || p->length % a || p->shift % a) return DM_E_INVALID_PLAN;
    if (p->length < a || p->shift < DM_MIB) return DM_E_INVALID_PLAN;
    /* The destination range [old_start + shift, old_start + shift + length) must not wrap. */
    if (p->old_start > UINT64_MAX - p->shift) return DM_E_INVALID_PLAN;
    if (p->old_start + p->shift > UINT64_MAX - p->length) return DM_E_INVALID_PLAN;
    if (p->extent_count > 0 && !p->extents) return DM_E_INVALID_PLAN;
    for (uint32_t i = 0; i < p->extent_count; i++) {
        const dm_extent *e = &p->extents[i];
        if (e->offset > p->length || e->length > p->length - e->offset) return DM_E_INVALID_PLAN;
    }
    return DM_OK;
}

static int cmp_range(const void *x, const void *y)
{
    const dm_extent *a = (const dm_extent *)x, *b = (const dm_extent *)y;
    return (a->offset > b->offset) - (a->offset < b->offset);
}

/* Ranges are stored as {lo, hi} in {offset, length} fields while merging. */
static uint64_t min_u64(uint64_t a, uint64_t b) { return a < b ? a : b; }

void dm_free(void *p) { free(p); }

dm_status dm_chunks_build(const dm_move_plan *p, dm_extent **out, uint32_t *out_count)
{
    if (!out || !out_count) return DM_E_INVALID_ARG;
    *out = NULL;
    *out_count = 0;
    dm_status st = dm_plan_validate(p);
    if (st != DM_OK) return st;

    const uint64_t a = align_unit(p);
    const uint64_t chunk = min_u64(DM_MAX_CHUNK, p->shift);
    const size_t nr = (size_t)p->extent_count + 2u;

    dm_extent *r = (dm_extent *)malloc(nr * sizeof *r);
    if (!r) return DM_E_NOMEM;

    size_t m = 0;
    const uint64_t ht = min_u64(DM_HEAD_TAIL, p->length);
    r[m].offset = 0;                r[m].length = ht;        m++;
    r[m].offset = p->length - ht;   r[m].length = p->length; m++;
    for (uint32_t i = 0; i < p->extent_count; i++) {
        const dm_extent *e = &p->extents[i];
        if (e->length == 0) continue;
        uint64_t lo = e->offset / a * a;
        uint64_t end = e->offset + e->length;              /* <= length, validated */
        uint64_t hi = end % a ? end + (a - end % a) : end; /* length is a multiple of a */
        if (hi > p->length) hi = p->length;
        r[m].offset = lo; r[m].length = hi; m++;
    }
    /* Head/tail are already aligned: 1 MiB multiple of a, length - ht multiple of a. */
    qsort(r, m, sizeof *r, cmp_range);

    size_t k = 0; /* merge in place; adjacent ranges merge too */
    for (size_t i = 1; i < m; i++) {
        if (r[i].offset <= r[k].length) {
            if (r[i].length > r[k].length) r[k].length = r[i].length;
        } else {
            r[++k] = r[i];
        }
    }
    m = k + 1;

    uint64_t total = 0;
    for (size_t i = 0; i < m; i++) {
        uint64_t span = r[i].length - r[i].offset;
        total += span / chunk + (span % chunk ? 1u : 0u);
    }
    if (total > UINT32_MAX) { free(r); return DM_E_NOMEM; }

    dm_extent *c = (dm_extent *)malloc((size_t)total * sizeof *c);
    if (!c) { free(r); return DM_E_NOMEM; }

    size_t n = 0;
    for (size_t i = m; i-- > 0;) {
        uint64_t lo = r[i].offset, end = r[i].length;
        while (end > lo) {
            uint64_t sz = min_u64(chunk, end - lo);
            end -= sz;
            c[n].offset = end;
            c[n].length = sz;
            n++;
        }
    }
    free(r);
    *out = c;
    *out_count = (uint32_t)n;
    return DM_OK;
}
