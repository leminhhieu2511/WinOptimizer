/*
 * Sidecar format (UTF-8 text, one item per line):
 *   attributes 0x<16 hex>
 *   partition <32 hex: the 16 GUID bytes as stored>
 *   volume \\?\Volume{...}\
 *   mount <path>            (0..8 lines)
 */
#include "cli_restore.h"
#include <io.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

uint64_t cli_hidden_attributes(uint64_t original)
{
    return original | CLI_HIDE_ATTRS;
}

void cli_sidecar_path(const wchar_t *journal, wchar_t *out, size_t cap)
{
    swprintf(out, cap, L"%ls.cli-restore", journal);
}

int cli_sidecar_write(const wchar_t *path, const restore_info *ri)
{
    FILE *f = _wfopen(path, L"w, ccs=UTF-8");
    if (!f) return 0;
    fwprintf(f, L"attributes 0x%016llX\npartition ", (unsigned long long)ri->attributes);
    for (int i = 0; i < 16; i++) fwprintf(f, L"%02X", ri->partition_guid[i]);
    fwprintf(f, L"\nvolume %ls\n", ri->volume);
    for (int i = 0; i < ri->mount_count; i++) fwprintf(f, L"mount %ls\n", ri->mounts[i]);
    int ok = fflush(f) == 0 && _commit(_fileno(f)) == 0;
    return fclose(f) == 0 && ok;
}

static int hexval(wchar_t c)
{
    if (c >= L'0' && c <= L'9') return c - L'0';
    if (c >= L'a' && c <= L'f') return c - L'a' + 10;
    if (c >= L'A' && c <= L'F') return c - L'A' + 10;
    return -1;
}

int cli_sidecar_read(const wchar_t *path, restore_info *ri)
{
    memset(ri, 0, sizeof *ri);
    FILE *f = _wfopen(path, L"r, ccs=UTF-8");
    if (!f) return 0;
    wchar_t line[CLI_PATH_MAX + 32];
    int have_attr = 0, have_part = 0;
    while (fgetws(line, CLI_PATH_MAX + 32, f)) {
        line[wcscspn(line, L"\r\n")] = 0;
        if (wcsncmp(line, L"attributes ", 11) == 0) {
            wchar_t *end = NULL;
            ri->attributes = _wcstoui64(line + 11, &end, 0);
            have_attr = end && end != line + 11 && *end == 0;
        } else if (wcsncmp(line, L"partition ", 10) == 0) {
            const wchar_t *h = line + 10;
            have_part = wcslen(h) == 32;
            for (int i = 0; have_part && i < 16; i++) {
                int hi = hexval(h[2 * i]), lo = hexval(h[2 * i + 1]);
                if (hi < 0 || lo < 0) have_part = 0;
                else ri->partition_guid[i] = (uint8_t)(hi << 4 | lo);
            }
        } else if (wcsncmp(line, L"volume ", 7) == 0) {
            wcscpy_s(ri->volume, CLI_PATH_MAX, line + 7);
        } else if (wcsncmp(line, L"mount ", 6) == 0 && ri->mount_count < CLI_MAX_MOUNTS) {
            wcscpy_s(ri->mounts[ri->mount_count++], CLI_PATH_MAX, line + 6);
        }
    }
    fclose(f);
    return have_attr && have_part && ri->volume[0];
}
