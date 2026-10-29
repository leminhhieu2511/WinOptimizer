/*
 * Crash-safe journal. Layout (all little-endian):
 *
 * Header, written once by dm_journal_create:
 *   0   magic "WODMJRN1" (8)      8  version u32 = 1     12 header_size u32 (used bytes rounded to 4096)
 *   16  move_count u32            20 app_len u32         24 app blob (app_len bytes)
 *   then per move: disk_guid[16] partition_guid[16] sector_size u32 old_start u64 length u64 shift u64
 *                  chunk_count u64, chunk_count * { offset u64, length u64 }
 *   then CRC32C (u32) of every preceding header byte; zero padding up to header_size.
 * Slots, 4096 bytes each at header_size and header_size + 4096:
 *   0 seq u64, 8 step u32, 12 move_index u32, 16 chunks_done u64, 24 last_hash u64, 4092 crc32c u32
 *   (CRC covers bytes 0..4091, i.e. the fields plus zero padding, so a write torn after a sector
 *   boundary can never produce a valid slot).
 */
#include <diskmove/dm_core.h>
#include <stdlib.h>
#include <string.h>

#define HDR_FIXED   24u
#define MOVE_FIXED  68u          /* 16+16+4+8+8+8+8 */
#define SLOT_SIZE   4096u
#define SLOT_CRC    (SLOT_SIZE - 4u)
#define HDR_ALIGN   4096u
#define HDR_MAX     (1u << 30)

static const uint8_t k_magic[8] = { 'W', 'O', 'D', 'M', 'J', 'R', 'N', '1' };

static void put32(uint8_t *p, uint32_t v) { for (int i = 0; i < 4; i++) p[i] = (uint8_t)(v >> (8 * i)); }
static void put64(uint8_t *p, uint64_t v) { for (int i = 0; i < 8; i++) p[i] = (uint8_t)(v >> (8 * i)); }
static uint32_t get32(const uint8_t *p) { uint32_t v = 0; for (int i = 3; i >= 0; i--) v = (v << 8) | p[i]; return v; }
static uint64_t get64(const uint8_t *p) { uint64_t v = 0; for (int i = 7; i >= 0; i--) v = (v << 8) | p[i]; return v; }

typedef struct slot { uint64_t seq; uint32_t step, move_index; uint64_t chunks_done, last_hash; } slot;

typedef struct hdr {
    uint8_t *buf;                       /* NULL when only the prefix was loaded */
    uint32_t size, move_count, app_len;
    uint64_t move_off[DM_MAX_MOVES];    /* offset of each move record */
    uint64_t move_chunks[DM_MAX_MOVES];
} hdr;

static void slot_encode(uint8_t *b, const slot *s)
{
    memset(b, 0, SLOT_SIZE);
    put64(b, s->seq); put32(b + 8, s->step); put32(b + 12, s->move_index);
    put64(b + 16, s->chunks_done); put64(b + 24, s->last_hash);
    put32(b + SLOT_CRC, dm_crc32c(0, b, SLOT_CRC));
}

static int slot_decode(const uint8_t *b, slot *s)
{
    if (get32(b + SLOT_CRC) != dm_crc32c(0, b, SLOT_CRC)) return 0;
    s->seq = get64(b);
    if (s->seq < 1) return 0;
    s->step = get32(b + 8); s->move_index = get32(b + 12);
    s->chunks_done = get64(b + 16); s->last_hash = get64(b + 24);
    return 1;
}

static dm_status flush_io(const dm_io *j) { return j->flush ? j->flush(j->ctx) : DM_OK; }

/* Reads the 4096-byte prefix and validates magic/version/header_size only. */
static dm_status hdr_prefix(const dm_io *j, uint8_t *prefix, uint32_t *header_size)
{
    dm_status s = j->read(j->ctx, 0, prefix, HDR_ALIGN);
    if (s != DM_OK) return s;
    if (memcmp(prefix, k_magic, 8) != 0 || get32(prefix + 8) != 1) return DM_E_JOURNAL_CORRUPT;
    uint32_t hs = get32(prefix + 12);
    if (hs < HDR_ALIGN || hs % HDR_ALIGN || hs > HDR_MAX) return DM_E_JOURNAL_CORRUPT;
    *header_size = hs;
    return DM_OK;
}

/* Loads and fully validates the header (caller frees h->buf). */
static dm_status hdr_load(const dm_io *j, hdr *h)
{
    memset(h, 0, sizeof *h);
    uint8_t prefix[HDR_ALIGN];
    dm_status s = hdr_prefix(j, prefix, &h->size);
    if (s != DM_OK) return s;
    h->buf = (uint8_t *)malloc(h->size);
    if (!h->buf) return DM_E_NOMEM;
    s = j->read(j->ctx, 0, h->buf, h->size);
    if (s != DM_OK) goto fail;

    h->move_count = get32(h->buf + 16);
    h->app_len = get32(h->buf + 20);
    if (h->move_count < 1 || h->move_count > DM_MAX_MOVES) goto corrupt;
    uint64_t pos = HDR_FIXED;
    if (h->app_len > h->size - pos) goto corrupt;
    pos += h->app_len;
    for (uint32_t i = 0; i < h->move_count; i++) {
        if (pos + MOVE_FIXED > h->size) goto corrupt;
        h->move_off[i] = pos;
        uint64_t cc = get64(h->buf + pos + 60);
        pos += MOVE_FIXED;
        if (cc > (h->size - pos) / 16) goto corrupt;
        h->move_chunks[i] = cc;
        pos += cc * 16;
    }
    if (pos + 4 > h->size) goto corrupt;
    if (get32(h->buf + pos) != dm_crc32c(0, h->buf, (size_t)pos)) goto corrupt;
    return DM_OK;
corrupt:
    s = DM_E_JOURNAL_CORRUPT;
fail:
    free(h->buf);
    h->buf = NULL;
    return s;
}

/* Reads both slots; picks the newest valid one in *cur and the slot index to write next in *target. */
static dm_status slots_read(const dm_io *j, uint32_t header_size, slot *cur, int *target)
{
    uint8_t b[2][SLOT_SIZE];
    slot sl[2];
    int ok[2];
    for (int i = 0; i < 2; i++) {
        dm_status s = j->read(j->ctx, (uint64_t)header_size + (uint64_t)i * SLOT_SIZE, b[i], SLOT_SIZE);
        if (s != DM_OK) return s;
        ok[i] = slot_decode(b[i], &sl[i]);
    }
    if (!ok[0] && !ok[1]) return DM_E_JOURNAL_CORRUPT;
    int newest = !ok[0] ? 1 : !ok[1] ? 0 : (sl[1].seq > sl[0].seq ? 1 : 0);
    *cur = sl[newest];
    *target = !ok[0] ? 0 : !ok[1] ? 1 : (sl[0].seq < sl[1].seq ? 0 : 1);
    return DM_OK;
}

static dm_status slot_write(const dm_io *j, uint32_t header_size, int target, const slot *s)
{
    uint8_t b[SLOT_SIZE];
    slot_encode(b, s);
    dm_status st = j->write(j->ctx, (uint64_t)header_size + (uint64_t)target * SLOT_SIZE, b, SLOT_SIZE);
    if (st != DM_OK) return st;
    return flush_io(j);
}

/* Read-modify-write of the newest slot into the other slot. mode 0: step; mode 1: progress. */
static dm_status slot_update(const dm_io *j, int mode, uint32_t step, uint32_t move_index,
                             uint64_t chunks_done, uint64_t last_hash)
{
    uint8_t prefix[HDR_ALIGN];
    uint32_t hs;
    dm_status s = hdr_prefix(j, prefix, &hs);
    if (s != DM_OK) return s;
    slot cur; int target;
    s = slots_read(j, hs, &cur, &target);
    if (s != DM_OK) return s;
    slot n = cur;
    n.seq = cur.seq + 1;
    if (mode == 0) n.step = step;
    else { n.move_index = move_index; n.chunks_done = chunks_done; n.last_hash = last_hash; }
    return slot_write(j, hs, target, &n);
}

dm_status dm_journal_create(const dm_io *j, const dm_move_plan *moves, uint32_t move_count,
                            const void *app_blob, uint32_t app_len)
{
    if (!j || !j->read || !j->write || !moves || move_count < 1 || move_count > DM_MAX_MOVES) return DM_E_INVALID_ARG;
    if (app_len && !app_blob) return DM_E_INVALID_ARG;
    if (app_len > HDR_MAX / 2) return DM_E_INVALID_ARG;

    dm_extent *chunks[DM_MAX_MOVES] = { NULL, NULL };
    uint32_t counts[DM_MAX_MOVES] = { 0, 0 };
    uint8_t *buf = NULL;
    dm_status s = DM_OK;

    for (uint32_t i = 0; i < move_count && s == DM_OK; i++) {
        s = dm_plan_validate(&moves[i]);
        if (s == DM_OK) s = dm_chunks_build(&moves[i], &chunks[i], &counts[i]);
    }
    if (s != DM_OK) goto done;

    uint64_t used = HDR_FIXED + (uint64_t)app_len;
    for (uint32_t i = 0; i < move_count; i++) used += MOVE_FIXED + (uint64_t)counts[i] * 16;
    used += 4;
    uint64_t hs = (used + HDR_ALIGN - 1) / HDR_ALIGN * HDR_ALIGN;
    if (hs > HDR_MAX) { s = DM_E_INVALID_PLAN; goto done; }
    buf = (uint8_t *)calloc(1, (size_t)hs);
    if (!buf) { s = DM_E_NOMEM; goto done; }

    memcpy(buf, k_magic, 8);
    put32(buf + 8, 1); put32(buf + 12, (uint32_t)hs);
    put32(buf + 16, move_count); put32(buf + 20, app_len);
    if (app_len) memcpy(buf + HDR_FIXED, app_blob, app_len);
    uint8_t *p = buf + HDR_FIXED + app_len;
    for (uint32_t i = 0; i < move_count; i++) {
        const dm_move_plan *m = &moves[i];
        memcpy(p, m->disk_guid, 16); memcpy(p + 16, m->partition_guid, 16);
        put32(p + 32, m->sector_size);
        put64(p + 36, m->old_start); put64(p + 44, m->length); put64(p + 52, m->shift);
        put64(p + 60, counts[i]);
        p += MOVE_FIXED;
        for (uint32_t c = 0; c < counts[i]; c++, p += 16) {
            put64(p, chunks[i][c].offset); put64(p + 8, chunks[i][c].length);
        }
    }
    put32(p, dm_crc32c(0, buf, (size_t)(p - buf)));

    s = j->write(j->ctx, 0, buf, (uint32_t)hs);
    if (s == DM_OK) s = flush_io(j);
    if (s == DM_OK) {
        slot first = { 1, 0, 0, 0, 0 };
        s = slot_write(j, (uint32_t)hs, 0, &first);
    }
done:
    free(buf);
    for (uint32_t i = 0; i < DM_MAX_MOVES; i++) dm_free(chunks[i]);
    return s;
}

dm_status dm_journal_read(const dm_io *j, dm_journal_info *info,
                          void *app_blob, uint32_t app_cap, uint32_t *app_len)
{
    if (!j || !j->read || !info) return DM_E_INVALID_ARG;
    hdr h;
    dm_status s = hdr_load(j, &h);
    if (s != DM_OK) return s;
    slot cur; int target;
    s = slots_read(j, h.size, &cur, &target);
    if (s != DM_OK) { free(h.buf); return s; }

    memset(info, 0, sizeof *info);
    info->move_count = h.move_count;
    info->step = cur.step;
    info->move_index = cur.move_index;
    info->chunks_done = cur.chunks_done;
    info->seq = cur.seq;
    for (uint32_t i = 0; i < h.move_count; i++) info->chunk_count[i] = h.move_chunks[i];
    if (app_len) *app_len = h.app_len;
    if (h.app_len) {
        if (!app_blob || app_cap < h.app_len) { free(h.buf); return DM_E_INVALID_ARG; }
        memcpy(app_blob, h.buf + HDR_FIXED, h.app_len);
    }
    free(h.buf);
    return DM_OK;
}

dm_status dm_journal_set_step(const dm_io *j, uint32_t step)
{
    if (!j || !j->read || !j->write) return DM_E_INVALID_ARG;
    return slot_update(j, 0, step, 0, 0, 0);
}

dm_status dm_journal_progress(const dm_io *j, uint32_t move_index, uint64_t chunks_done, uint64_t last_hash)
{
    if (!j || !j->read || !j->write) return DM_E_INVALID_ARG;
    return slot_update(j, 1, 0, move_index, chunks_done, last_hash);
}

dm_status dm_journal_load_move(const dm_io *j, uint32_t move_index, dm_move_plan *plan_out,
                               dm_extent **chunks, uint32_t *chunk_count)
{
    if (!j || !j->read || !plan_out || !chunks || !chunk_count) return DM_E_INVALID_ARG;
    hdr h;
    dm_status s = hdr_load(j, &h);
    if (s != DM_OK) return s;
    if (move_index >= h.move_count) { free(h.buf); return DM_E_INVALID_ARG; }

    const uint8_t *p = h.buf + h.move_off[move_index];
    uint64_t cc = h.move_chunks[move_index];
    if (cc > 0xFFFFFFFFull) { free(h.buf); return DM_E_JOURNAL_CORRUPT; }
    dm_extent *out = (dm_extent *)malloc((cc ? (size_t)cc : 1) * sizeof *out);
    if (!out) { free(h.buf); return DM_E_NOMEM; }

    memset(plan_out, 0, sizeof *plan_out);
    memcpy(plan_out->disk_guid, p, 16); memcpy(plan_out->partition_guid, p + 16, 16);
    plan_out->sector_size = get32(p + 32);
    plan_out->old_start = get64(p + 36); plan_out->length = get64(p + 44); plan_out->shift = get64(p + 52);
    p += MOVE_FIXED;
    for (uint64_t c = 0; c < cc; c++, p += 16) { out[c].offset = get64(p); out[c].length = get64(p + 8); }
    free(h.buf);
    *chunks = out;
    *chunk_count = (uint32_t)cc;
    return DM_OK;
}
