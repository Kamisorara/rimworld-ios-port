// MonoMod expects a dynamically loaded libclrjit with an exported getJit. This
// runtime links RyuJIT into libcoreclr.dylib, leaving getJit as a local symbol.
// The shim exposes that exact JIT instance through the ABI MonoMod expects.
#include <mach-o/dyld.h>
#include <mach-o/loader.h>
#include <mach-o/nlist.h>
#include <stdint.h>
#include <string.h>

typedef void *(*get_jit_fn)(void);

static get_jit_fn resolve_static_get_jit(void)
{
    uint32_t count = _dyld_image_count();
    for (uint32_t i = 0; i < count; i++)
    {
        const char *image = _dyld_get_image_name(i);
        if (image == NULL)
            continue;
        const char *slash = strrchr(image, '/');
        const char *name = slash != NULL ? slash + 1 : image;
        if (strcmp(name, "libcoreclr.dylib") != 0)
            continue;

        const struct mach_header_64 *header =
            (const struct mach_header_64 *)_dyld_get_image_header(i);
        if (header == NULL || header->magic != MH_MAGIC_64)
            continue;

        intptr_t slide = _dyld_get_image_vmaddr_slide(i);
        const uint8_t *cursor = (const uint8_t *)(header + 1);
        const struct symtab_command *symtab = NULL;
        uint64_t textVmaddr = 0;
        uint64_t linkeditVmaddr = 0;
        uint64_t linkeditFileoff = 0;
        int hasText = 0;
        int hasLinkedit = 0;

        for (uint32_t cmd = 0; cmd < header->ncmds; cmd++)
        {
            const struct load_command *load = (const struct load_command *)cursor;
            if (load->cmd == LC_SEGMENT_64)
            {
                const struct segment_command_64 *segment =
                    (const struct segment_command_64 *)load;
                if (strcmp(segment->segname, "__TEXT") == 0)
                {
                    textVmaddr = segment->vmaddr;
                    hasText = 1;
                }
                else if (strcmp(segment->segname, "__LINKEDIT") == 0)
                {
                    linkeditVmaddr = segment->vmaddr;
                    linkeditFileoff = segment->fileoff;
                    hasLinkedit = 1;
                }
            }
            else if (load->cmd == LC_SYMTAB)
            {
                symtab = (const struct symtab_command *)load;
            }
            cursor += load->cmdsize;
        }

        if (symtab == NULL || !hasText || !hasLinkedit)
            continue;

        const uint8_t *linkedit =
            (const uint8_t *)header + (linkeditVmaddr - textVmaddr);
        const struct nlist_64 *symbols =
            (const struct nlist_64 *)(linkedit + (symtab->symoff - linkeditFileoff));
        const char *strings =
            (const char *)(linkedit + (symtab->stroff - linkeditFileoff));

        for (uint32_t j = 0; j < symtab->nsyms; j++)
        {
            if (symbols[j].n_value == 0)
                continue;
            const char *symbol = strings + symbols[j].n_un.n_strx;
            if (strcmp(symbol, "_getJit") == 0)
                return (get_jit_fn)(symbols[j].n_value + slide);
        }
    }
    return NULL;
}

__attribute__((visibility("default"))) void *getJit(void)
{
    static get_jit_fn resolved;
    if (resolved == NULL)
        resolved = resolve_static_get_jit();
    return resolved != NULL ? resolved() : NULL;
}
