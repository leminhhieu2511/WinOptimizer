#ifndef DM_TEST_H
#define DM_TEST_H
/*
 * Tiny test harness. Each test file test_<name>.c defines:
 *     void dm_run_<name>(void) { DM_RUN(test_a); DM_RUN(test_b); }
 * CMake builds one executable + one CTest test per test_*.c (see tests/CMakeLists.txt),
 * so adding a test file needs no other edits.
 */
#include <stdio.h>
#include <stdint.h>

extern int dm_test_failures;

#define DM_FAIL(...) do { \
    printf("  FAIL %s:%d: ", __FILE__, __LINE__); printf(__VA_ARGS__); printf("\n"); \
    dm_test_failures++; } while (0)

#define ASSERT_TRUE(c) do { if (!(c)) DM_FAIL("expected true: %s", #c); } while (0)
#define ASSERT_EQ_U32(a, b) do { uint32_t va_ = (uint32_t)(a), vb_ = (uint32_t)(b); \
    if (va_ != vb_) DM_FAIL("%s == %s (got 0x%08X, want 0x%08X)", #a, #b, (unsigned)va_, (unsigned)vb_); } while (0)
#define ASSERT_EQ_U64(a, b) do { uint64_t va_ = (uint64_t)(a), vb_ = (uint64_t)(b); \
    if (va_ != vb_) DM_FAIL("%s == %s (got 0x%016llX, want 0x%016llX)", #a, #b, \
        (unsigned long long)va_, (unsigned long long)vb_); } while (0)
#define ASSERT_EQ_INT(a, b) do { long long va_ = (long long)(a), vb_ = (long long)(b); \
    if (va_ != vb_) DM_FAIL("%s == %s (got %lld, want %lld)", #a, #b, va_, vb_); } while (0)

#define DM_RUN(fn) do { int before_ = dm_test_failures; fn(); \
    printf("[%s] %s\n", dm_test_failures == before_ ? " OK " : "FAIL", #fn); } while (0)

#endif