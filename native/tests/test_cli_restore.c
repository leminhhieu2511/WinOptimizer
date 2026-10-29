/* diskmove-cli restore sidecar + hide-attribute math (temp files only). */
#include "dm_test.h"
#include "cli_restore.h"
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>

static void temp_path(wchar_t *out, const wchar_t *tag)
{
    wchar_t dir[MAX_PATH];
    DWORD n = GetTempPathW(MAX_PATH, dir);
    ASSERT_TRUE(n > 0 && n < MAX_PATH);
    swprintf(out, MAX_PATH, L"%sdmcli_%s_%lu_%lu", dir, tag, GetCurrentProcessId(), GetTickCount());
}

static void hidden_attributes_keep_every_bit(void)
{
    ASSERT_EQ_U64(cli_hidden_attributes(0), 0xC000000000000000ull);
    /* Recovery partition: bit 0 (PLATFORM_REQUIRED) and NO_DRIVE_LETTER kept. */
    ASSERT_EQ_U64(cli_hidden_attributes(0x8000000000000001ull), 0xC000000000000001ull);
    ASSERT_EQ_U64(cli_hidden_attributes(0x1000000000000005ull), 0xD000000000000005ull);
    ASSERT_EQ_U64(cli_hidden_attributes(0xC000000000000000ull), 0xC000000000000000ull);   /* idempotent */
    ASSERT_EQ_U64(cli_hidden_attributes(UINT64_MAX), UINT64_MAX);
    ASSERT_EQ_U64(CLI_HIDE_ATTRS, DMW_GPT_ATTRIBUTE_HIDDEN | DMW_GPT_ATTRIBUTE_NO_DRIVE_LETTER);
}

static void sidecar_round_trip(void)
{
    wchar_t journal[MAX_PATH], side[MAX_PATH * 2];
    temp_path(journal, L"jr");
    cli_sidecar_path(journal, side, MAX_PATH * 2);
    ASSERT_TRUE(wcslen(side) == wcslen(journal) + wcslen(L".cli-restore"));
    ASSERT_TRUE(wcscmp(side + wcslen(journal), L".cli-restore") == 0);

    restore_info in, out;
    memset(&in, 0, sizeof in);
    in.attributes = 0x8000000000000001ull;
    for (int i = 0; i < 16; i++) in.partition_guid[i] = (uint8_t)(0xF0 - 7 * i);
    wcscpy_s(in.volume, CLI_PATH_MAX, L"\\\\?\\Volume{5c46a07b-d17e-47f1-9680-3d685b8245a3}\\");
    in.mount_count = 2;
    wcscpy_s(in.mounts[0], CLI_PATH_MAX, L"F:\\");
    wcscpy_s(in.mounts[1], CLI_PATH_MAX, L"C:\\mnt\\d\u00e9j\u00e0 vu\\");   /* non-ASCII survives UTF-8 */
    ASSERT_TRUE(cli_sidecar_write(side, &in));

    memset(&out, 0xCC, sizeof out);
    ASSERT_TRUE(cli_sidecar_read(side, &out));
    ASSERT_EQ_U64(out.attributes, in.attributes);
    ASSERT_TRUE(memcmp(out.partition_guid, in.partition_guid, 16) == 0);
    ASSERT_TRUE(wcscmp(out.volume, in.volume) == 0);
    ASSERT_EQ_INT(out.mount_count, 2);
    ASSERT_TRUE(wcscmp(out.mounts[0], in.mounts[0]) == 0);
    ASSERT_TRUE(wcscmp(out.mounts[1], in.mounts[1]) == 0);

    /* No mount points (e.g. a volume reached only by GUID path). */
    in.mount_count = 0;
    in.attributes = 0;
    ASSERT_TRUE(cli_sidecar_write(side, &in));
    ASSERT_TRUE(cli_sidecar_read(side, &out));
    ASSERT_EQ_U64(out.attributes, 0);
    ASSERT_EQ_INT(out.mount_count, 0);
    DeleteFileW(side);
}

static void write_text(const wchar_t *path, const char *text)
{
    FILE *f = _wfopen(path, L"wb");
    ASSERT_TRUE(f != NULL);
    if (f) { fputs(text, f); fclose(f); }
}

static void sidecar_rejects_incomplete(void)
{
    wchar_t side[MAX_PATH];
    temp_path(side, L"bad");
    restore_info out;
    ASSERT_TRUE(!cli_sidecar_read(side, &out));   /* missing file */
    /* No partition line (old format) -> not usable. */
    write_text(side, "attributes 0x8000000000000001\nvolume \\\\?\\Volume{x}\\\nmount F:\\\n");
    ASSERT_TRUE(!cli_sidecar_read(side, &out));
    /* Bad attributes value. */
    write_text(side, "attributes zz\npartition 00112233445566778899AABBCCDDEEFF\nvolume \\\\?\\Volume{x}\\\n");
    ASSERT_TRUE(!cli_sidecar_read(side, &out));
    /* Short partition GUID. */
    write_text(side, "attributes 0x1\npartition 0011\nvolume \\\\?\\Volume{x}\\\n");
    ASSERT_TRUE(!cli_sidecar_read(side, &out));
    /* No volume. */
    write_text(side, "attributes 0x1\npartition 00112233445566778899AABBCCDDEEFF\n");
    ASSERT_TRUE(!cli_sidecar_read(side, &out));
    /* Complete, CRLF line ends. */
    write_text(side, "attributes 0x1\r\npartition 00112233445566778899aabbccddeeff\r\nvolume \\\\?\\Volume{x}\\\r\n");
    ASSERT_TRUE(cli_sidecar_read(side, &out));
    ASSERT_EQ_U64(out.attributes, 1);
    ASSERT_EQ_U32(out.partition_guid[15], 0xFF);
    ASSERT_TRUE(wcscmp(out.volume, L"\\\\?\\Volume{x}\\") == 0);
    DeleteFileW(side);
}

void dm_run_cli_restore(void)
{
    DM_RUN(hidden_attributes_keep_every_bit);
    DM_RUN(sidecar_round_trip);
    DM_RUN(sidecar_rejects_incomplete);
}
