#include "dm_test.h"
#include <diskmove/dm_core.h>
#include <string.h>

static void test_crc32c_known(void)
{
    ASSERT_EQ_U32(dm_crc32c(0, "123456789", 9), 0xE3069283u);
}

static void test_crc32c_incremental(void)
{
    const char *a = "The quick brown fox jumps over the lazy dog";
    size_t n = strlen(a);
    for (size_t n1 = 0; n1 <= n; n1++) {
        uint32_t whole = dm_crc32c(0, a, n);
        uint32_t part = dm_crc32c(dm_crc32c(0, a, n1), a + n1, n - n1);
        ASSERT_EQ_U32(part, whole);
    }
}

static void test_xxh64_known(void)
{
    ASSERT_EQ_U64(dm_xxh64("", 0, 0), 0xEF46DB3751D8E999ull);
    ASSERT_EQ_U64(dm_xxh64("abc", 3, 0), 0x44BC2CF5AD770999ull);
}

static void test_xxh64_long_input(void)
{
    /* Exercise the 32-byte stripe path; just check determinism and seed sensitivity. */
    unsigned char buf[100];
    for (size_t i = 0; i < sizeof buf; i++) buf[i] = (unsigned char)(i * 7 + 1);
    ASSERT_EQ_U64(dm_xxh64(buf, sizeof buf, 0), dm_xxh64(buf, sizeof buf, 0));
    ASSERT_TRUE(dm_xxh64(buf, sizeof buf, 0) != dm_xxh64(buf, sizeof buf, 1));
}

void dm_run_hash(void)
{
    DM_RUN(test_crc32c_known);
    DM_RUN(test_crc32c_incremental);
    DM_RUN(test_xxh64_known);
    DM_RUN(test_xxh64_long_input);
}