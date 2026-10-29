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

#ifdef __cplusplus
}
#endif

#endif /* DISKMOVE_DM_CORE_H */