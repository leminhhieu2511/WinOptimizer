#ifndef DISKMOVE_CLI_RESTORE_H
#define DISKMOVE_CLI_RESTORE_H
/* diskmove-cli restore sidecar "<journal>.cli-restore" + hide-attribute math (unit-tested on its own). */
#include <diskmove/dmw_api.h>
#include <stddef.h>
#include <stdint.h>
#include <wchar.h>

#define CLI_MAX_MOUNTS 8
#define CLI_PATH_MAX   260
#define CLI_HIDE_ATTRS (DMW_GPT_ATTRIBUTE_HIDDEN | DMW_GPT_ATTRIBUTE_NO_DRIVE_LETTER)

typedef struct restore_info {
    uint64_t attributes;                       /* exact original GPT attributes */
    uint8_t  partition_guid[16];               /* the partition they belong to */
    wchar_t  volume[CLI_PATH_MAX];             /* \\?\Volume{...}\ */
    int      mount_count;
    wchar_t  mounts[CLI_MAX_MOUNTS][CLI_PATH_MAX];
} restore_info;

/* Attributes to set while moving: every original bit kept (incl. bit 0), plus HIDDEN | NO_DRIVE_LETTER. */
uint64_t cli_hidden_attributes(uint64_t original);

void cli_sidecar_path(const wchar_t *journal, wchar_t *out, size_t cap);
/* Writes and flushes to disk (fflush + _commit). Returns 1 on success. */
int  cli_sidecar_write(const wchar_t *path, const restore_info *ri);
/* Returns 1 only if attributes, partition and volume lines were all present and valid. */
int  cli_sidecar_read(const wchar_t *path, restore_info *ri);

#endif
