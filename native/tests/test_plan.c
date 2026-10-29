#include "dm_test.h"
#include <diskmove/dm_core.h>
#include <stdlib.h>
#include <string.h>

#define KIB 1024ull
#define MIB (1024ull * KIB)
#define GIB (1024ull * MIB)

static dm_move_plan base_plan(const dm_extent *ex, uint32_t n)
{
    dm_move_plan p;
    memset(&p, 0, sizeof p);
    p.sector_size = 512;
    p.old_start = 1 * MIB;
    p.length = 64 * MIB;
    p.shift = 32 * MIB;
    p.extent_count = n;
    p.extents = ex;
    return p;
}

static void test_plan_rejects_bad_values(void)
{
    dm_extent ok = { 0, 1 * MIB };
    dm_move_plan p = base_plan(&ok, 1);
    ASSERT_EQ_INT(dm_plan_validate(&p), DM_OK);

    p = base_plan(&ok, 1); p.shift = 0;
    ASSERT_EQ_INT(dm_plan_validate(&p), DM_E_INVALID_PLAN);
    p = base_plan(&ok, 1); p.sector_size = 1024;
    ASSERT_EQ_INT(dm_plan_validate(&p), DM_E_INVALID_PLAN);
    p = base_plan(&ok, 1); p.old_start = 4096 + 512;
    ASSERT_EQ_INT(dm_plan_validate(&p), DM_E_INVALID_PLAN);
    p = base_plan(&ok, 1); p.length = 64 * MIB + 512;
    ASSERT_EQ_INT(dm_plan_validate(&p), DM_E_INVALID_PLAN);
    p = base_plan(&ok, 1); p.shift = 1 * MIB - 4096;
    ASSERT_EQ_INT(dm_plan_validate(&p), DM_E_INVALID_PLAN);
    p = base_plan(&ok, 1); p.shift = 1 * MIB + 512;
    ASSERT_EQ_INT(dm_plan_validate(&p), DM_E_INVALID_PLAN);
    p = base_plan(&ok, 1); p.length = 0;
    ASSERT_EQ_INT(dm_plan_validate(&p), DM_E_INVALID_PLAN);

    dm_extent past = { 63 * MIB, 2 * MIB };
    p = base_plan(&past, 1);
    ASSERT_EQ_INT(dm_plan_validate(&p), DM_E_INVALID_PLAN);
    dm_extent wrap = { 4096, UINT64_MAX };
    p = base_plan(&wrap, 1);
    ASSERT_EQ_INT(dm_plan_validate(&p), DM_E_INVALID_PLAN);
    p = base_plan(NULL, 1);
    ASSERT_EQ_INT(dm_plan_validate(&p), DM_E_INVALID_PLAN);
    ASSERT_EQ_INT(dm_plan_validate(NULL), DM_E_INVALID_ARG);

    /* 4096-byte sectors: A = 4096 either way; 512-byte-only multiples are rejected. */
    p = base_plan(&ok, 1); p.sector_size = 4096; p.old_start = 8192;
    ASSERT_EQ_INT(dm_plan_validate(&p), DM_OK);
}

static void test_plan_accepts_4k_aligned_non_mib_start(void)
{
    dm_move_plan p = base_plan(NULL, 0);
    p.old_start = 32256; /* 63 * 512 */
    ASSERT_EQ_INT(dm_plan_validate(&p), DM_E_INVALID_PLAN);
    p.old_start = 4096ull * 63;
    ASSERT_EQ_INT(dm_plan_validate(&p), DM_OK);
}

static void test_chunks_merge_and_head_tail(void)
{
    dm_extent ex[2] = { { 10 * MIB, 1 * MIB }, { 10 * MIB + 512 * KIB, 2 * MIB } };
    dm_move_plan p = base_plan(ex, 2);
    dm_extent *c = NULL; uint32_t n = 0;
    ASSERT_EQ_INT(dm_chunks_build(&p, &c, &n), DM_OK);
    ASSERT_EQ_INT(n, 3);
    if (n == 3) {
        ASSERT_EQ_U64(c[0].offset, 63 * MIB); ASSERT_EQ_U64(c[0].length, 1 * MIB);
        ASSERT_EQ_U64(c[1].offset, 10 * MIB); ASSERT_EQ_U64(c[1].length, 2 * MIB + 512 * KIB);
        ASSERT_EQ_U64(c[2].offset, 0);        ASSERT_EQ_U64(c[2].length, 1 * MIB);
    }
    dm_free(c);
}

static void test_chunks_unaligned_extent_widened(void)
{
    dm_extent ex = { 10 * MIB + 100, 10 };
    dm_move_plan p = base_plan(&ex, 1);
    dm_extent *c = NULL; uint32_t n = 0;
    ASSERT_EQ_INT(dm_chunks_build(&p, &c, &n), DM_OK);
    ASSERT_EQ_INT(n, 3);
    if (n == 3) {
        ASSERT_EQ_U64(c[1].offset, 10 * MIB);
        ASSERT_EQ_U64(c[1].length, 4096);
    }
    dm_free(c);
}

static void test_chunks_respect_shift_limit(void)
{
    dm_extent ex = { 10 * MIB, 40 * MIB };
    dm_move_plan p = base_plan(&ex, 1);
    p.shift = 4 * MIB;
    dm_extent *c = NULL; uint32_t n = 0;
    ASSERT_EQ_INT(dm_chunks_build(&p, &c, &n), DM_OK);
    ASSERT_TRUE(n > 0);
    uint64_t total = 0;
    for (uint32_t i = 0; i < n; i++) {
        ASSERT_TRUE(c[i].length <= 4 * MIB);
        ASSERT_TRUE(c[i].length > 0);
        total += c[i].length;
        if (i) ASSERT_TRUE(c[i].offset + c[i].length <= c[i - 1].offset);
    }
    ASSERT_EQ_U64(total, 40 * MIB + 2 * MIB); /* [0,1) + [10,50) + [63,64) */
    dm_free(c);
}

static void test_chunks_max_16mib(void)
{
    dm_extent ex = { 0, 64 * MIB };
    dm_move_plan p = base_plan(&ex, 1);
    p.shift = 1 * GIB;
    dm_extent *c = NULL; uint32_t n = 0;
    ASSERT_EQ_INT(dm_chunks_build(&p, &c, &n), DM_OK);
    ASSERT_EQ_INT(n, 4);
    for (uint32_t i = 0; i < n; i++) ASSERT_TRUE(c[i].length <= 16 * MIB);
    dm_free(c);
}

static void test_chunks_no_extents(void)
{
    dm_move_plan p = base_plan(NULL, 0);
    dm_extent *c = NULL; uint32_t n = 0;
    ASSERT_EQ_INT(dm_chunks_build(&p, &c, &n), DM_OK);
    ASSERT_EQ_INT(n, 2);
    dm_free(c);
}

static void test_chunks_whole_partition(void)
{
    dm_extent ex = { 0, 150 * GIB };
    dm_move_plan p = base_plan(&ex, 1);
    p.length = 150 * GIB;
    p.shift = 100 * GIB;
    dm_extent *c = NULL; uint32_t n = 0;
    ASSERT_EQ_INT(dm_chunks_build(&p, &c, &n), DM_OK);
    ASSERT_EQ_U64(n, 150 * GIB / (16 * MIB));
    if (c && n) {
        ASSERT_EQ_U64(c[0].offset, 150 * GIB - 16 * MIB);
        ASSERT_EQ_U64(c[n - 1].offset, 0);
    }
    dm_free(c);
}

void dm_run_plan(void)
{
    DM_RUN(test_plan_rejects_bad_values);
    DM_RUN(test_plan_accepts_4k_aligned_non_mib_start);
    DM_RUN(test_chunks_merge_and_head_tail);
    DM_RUN(test_chunks_unaligned_extent_widened);
    DM_RUN(test_chunks_respect_shift_limit);
    DM_RUN(test_chunks_max_16mib);
    DM_RUN(test_chunks_no_extents);
    DM_RUN(test_chunks_whole_partition);
}
