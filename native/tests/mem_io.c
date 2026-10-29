#include "mem_io.h"
#include <stdlib.h>
#include <string.h>

void mem_io_init(mem_io *m, mem_fault *fault)
{
    memset(m, 0, sizeof *m);
    m->fault = fault;
}

void mem_io_free(mem_io *m)
{
    free(m->data);
    m->data = NULL;
    m->size = m->cap = 0;
}

static dm_status mem_grow(mem_io *m, uint64_t need)
{
    if (need <= m->size) return DM_OK;
    if (need > m->cap) {
        uint64_t nc = m->cap ? m->cap : 4096;
        while (nc < need) nc *= 2;
        uint8_t *p = (uint8_t *)realloc(m->data, (size_t)nc);
        if (!p) return DM_E_NOMEM;
        m->data = p;
        m->cap = nc;
    }
    memset(m->data + m->size, 0, (size_t)(need - m->size));
    m->size = need;
    return DM_OK;
}

static dm_status mem_read(void *ctx, uint64_t off, void *buf, uint32_t len)
{
    mem_io *m = (mem_io *)ctx;
    memset(buf, 0, len);
    if (off < m->size) {
        uint64_t n = m->size - off;
        if (n > len) n = len;
        memcpy(buf, m->data + off, (size_t)n);
    }
    return DM_OK;
}

static dm_status mem_write(void *ctx, uint64_t off, const void *buf, uint32_t len)
{
    mem_io *m = (mem_io *)ctx;
    uint32_t n = len;
    dm_status result = DM_OK;
    if (m->fault) {
        if (m->fault->tripped) return DM_E_IO;
        m->fault->writes++;
        if (m->fault->fail_after_writes && m->fault->writes == m->fault->fail_after_writes) {
            n = (len / 2) & ~511u;
            m->fault->tripped = 1;
            result = DM_E_IO;
        }
    }
    if (n) {
        dm_status s = mem_grow(m, off + n);
        if (s != DM_OK) return s;
        memcpy(m->data + off, buf, n);
    }
    return result;
}

static dm_status mem_flush(void *ctx)
{
    mem_io *m = (mem_io *)ctx;
    return (m->fault && m->fault->tripped) ? DM_E_IO : DM_OK;
}

dm_io mem_io_as_dm_io(mem_io *m)
{
    dm_io io;
    io.ctx = m;
    io.read = mem_read;
    io.write = mem_write;
    io.flush = mem_flush;
    return io;
}
