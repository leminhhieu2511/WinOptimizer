#ifndef DM_MEM_IO_H
#define DM_MEM_IO_H
/*
 * In-memory dm_io with crash injection. Several mem_io instances may share one mem_fault so a single
 * write counter spans e.g. the disk io and the journal io.
 */
#include <diskmove/dm_core.h>

typedef struct mem_fault {
    uint64_t writes;             /* total writes seen (all instances sharing this controller) */
    uint64_t fail_after_writes;  /* 0 = never; the Nth write is torn (first half, 512-aligned) + DM_E_IO */
    int      tripped;            /* once set, every later write and flush fails; reads keep working */
} mem_fault;

typedef struct mem_io {
    uint8_t   *data;
    uint64_t   size, cap;
    mem_fault *fault;            /* may be NULL */
} mem_io;

void   mem_io_init(mem_io *m, mem_fault *fault);
void   mem_io_free(mem_io *m);
dm_io  mem_io_as_dm_io(mem_io *m);

#endif
