#include "dm_test.h"

/* DM_SUITE is defined per executable by CMake (the test file's base name). */
#define DM_CAT_(a, b) a##b
#define DM_CAT(a, b) DM_CAT_(a, b)
#define DM_RUN_FN DM_CAT(dm_run_, DM_SUITE)

int dm_test_failures = 0;
extern void DM_RUN_FN(void);

int main(void)
{
    DM_RUN_FN();
    if (dm_test_failures) {
        printf("%d assertion(s) failed\n", dm_test_failures);
        return 1;
    }
    printf("all passed\n");
    return 0;
}