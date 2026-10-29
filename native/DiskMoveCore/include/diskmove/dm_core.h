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

#ifdef __cplusplus
}
#endif

#endif /* DISKMOVE_DM_CORE_H */