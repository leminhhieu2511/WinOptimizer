#ifndef DISKMOVE_DM_CORE_H
#define DISKMOVE_DM_CORE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum dm_status {
    DM_OK = 0, DM_E_INVALID_ARG = 1, DM_E_INVALID_PLAN = 2, DM_E_IO = 3, DM_E_VERIFY = 4,
    DM_E_JOURNAL_CORRUPT = 5, DM_E_JOURNAL_MISMATCH = 6, DM_E_STOPPED = 7, DM_E_NOMEM = 8,
    DM_E_NOT_FOUND = 9, DM_E_ACCESS = 10, DM_E_LOCKED = 11
} dm_status;

/* CRC32C (Castagnoli). Pass seed 0 for the first call; chain the result to continue. */
uint32_t dm_crc32c(uint32_t seed, const void *data, size_t len);

/* XXH64 per the reference specification. */
uint64_t dm_xxh64(const void *data, size_t len, uint64_t seed);

/* ---- Move plan validation and chunking ---- */
typedef struct dm_extent { uint64_t offset, length; } dm_extent;   /* bytes from partition start */
typedef struct dm_move_plan {
    uint8_t  disk_guid[16], partition_guid[16];
    uint32_t sector_size;              /* 512 or 4096 */
    uint64_t old_start, length, shift; /* bytes; partition moves right by shift */
    uint32_t extent_count;
    const dm_extent *extents;          /* used regions; may overlap, unsorted */
} dm_move_plan;

/* Alignment unit is A = max(4096, sector_size); old_start, length, shift must be multiples of A,
   length >= A, shift >= 1 MiB, every extent within [0, length) with no overflow. */
dm_status dm_plan_validate(const dm_move_plan *p);
/* Merge extents + 1 MiB head/tail, widen to A, split into chunks <= min(16 MiB, shift),
   sorted by strictly descending offset. Caller frees *out with dm_free. */
dm_status dm_chunks_build(const dm_move_plan *p, dm_extent **out, uint32_t *out_count);
void      dm_free(void *p);

/* ---- Crash-safe journal ---- */
typedef struct dm_io {
    void *ctx;
    dm_status (*read)(void *ctx, uint64_t off, void *buf, uint32_t len);
    dm_status (*write)(void *ctx, uint64_t off, const void *buf, uint32_t len);
    dm_status (*flush)(void *ctx);
} dm_io;
typedef struct dm_journal_info {
    uint32_t move_count;
    uint32_t step;              /* step 0-7, defined by the C# side */
    uint32_t move_index;        /* move currently running */
    uint64_t chunks_done;       /* of move_index */
    uint64_t chunk_count[2];    /* total chunks of each move (0 for unused) */
    uint64_t seq;
} dm_journal_info;
#define DM_MAX_MOVES 2
dm_status dm_journal_create(const dm_io *j, const dm_move_plan *moves, uint32_t move_count,
                            const void *app_blob, uint32_t app_len);
/* If app_cap < app_len: *app_len is still set and DM_E_INVALID_ARG returned (retry with bigger buffer). */
dm_status dm_journal_read(const dm_io *j, dm_journal_info *info,
                          void *app_blob, uint32_t app_cap, uint32_t *app_len);
dm_status dm_journal_set_step(const dm_io *j, uint32_t step);
/* Internal (dm_move.c): stored plan (extents = NULL) + stored chunks (free with dm_free); progress write. */
dm_status dm_journal_load_move(const dm_io *j, uint32_t move_index, dm_move_plan *plan_out,
                               dm_extent **chunks, uint32_t *chunk_count);
dm_status dm_journal_progress(const dm_io *j, uint32_t move_index, uint64_t chunks_done, uint64_t last_hash);

/* ---- Backward chunk mover ---- */
/* Return non-zero to request a stop; the core only stops between two chunks. */
typedef int (*dm_progress_cb)(void *user, uint64_t bytes_done, uint64_t bytes_total);
/* Runs (or resumes) move move_index from the journal. On completion returns DM_OK and the journal slot
   has chunks_done == chunk_count. Moves run in order: a finished move returns DM_OK without I/O; starting
   move i+1 before move i is complete returns DM_E_JOURNAL_MISMATCH. progress may be NULL. */
dm_status dm_run_move(const dm_io *disk, const dm_io *journal, uint32_t move_index,
                      dm_progress_cb progress, void *user);

#ifdef __cplusplus
}
#endif

#endif /* DISKMOVE_DM_CORE_H */