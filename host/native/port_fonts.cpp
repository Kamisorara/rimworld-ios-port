// Font files are mapped, not read into the heap.
//
// ImFontAtlas::AddFontFromFileTTF reads the whole file into memory the atlas then owns, once for every font it is
// added to. The game sets up three sizes and adds the system's CJK font to each as a fallback (and again for every
// bold or italic variant it first needs): NotoSansCJK-Regular.ttc is 32 MiB, so 97 MiB of heap from the start for a
// file of which the rasterizer reads the tables and the glyphs that are shown.
// imgui_draw.cpp is compiled with ImFileLoadToMemory renamed to PortFontFileLoad (native/build.sh), which is here: the
// file is mapped read-only, once per path, and the mapping handed out as if it were the loaded copy. The atlas "frees"
// it like any other block, so the library's allocator functions are set to ones that leave the mappings alone. The
// mappings stay for the life of the process; their pages are the file's own, shared with whoever else has it open.
#include <fcntl.h>
#include <stdio.h>
#include <pthread.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#include "imgui.h"
#include "imgui_internal.h"

namespace
{
    struct MappedFont { char path[256]; char* data; size_t size; };
    MappedFont g_fonts[32];
    int g_fontCount; // entries are written whole before the count grows, so readers need no lock
    pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;

    bool IsMappedFont(void* p)
    {
        int count = __atomic_load_n(&g_fontCount, __ATOMIC_ACQUIRE);
        for (int i = 0; i < count; i++)
        {
            if ((char*)p >= g_fonts[i].data && (char*)p < g_fonts[i].data + g_fonts[i].size) return true;
        }
        return false;
    }

    void* PortAlloc(size_t size, void*) { return malloc(size); }
    void PortFree(void* p, void*) { if (p != nullptr && !IsMappedFont(p)) free(p); }

    struct SetAllocator { SetAllocator() { ImGui::SetAllocatorFunctions(PortAlloc, PortFree, nullptr); } } g_setAllocator;
}

void* PortFontFileLoad(const char* filename, const char* mode, size_t* out_file_size, int padding_bytes)
{
    if (filename == nullptr || padding_bytes != 0 || strlen(filename) >= sizeof g_fonts[0].path)
    {
        return ImFileLoadToMemory(filename, mode, out_file_size, padding_bytes);
    }
    pthread_mutex_lock(&g_lock);
    for (int i = 0; i < g_fontCount; i++)
    {
        if (strcmp(g_fonts[i].path, filename) == 0)
        {
            if (out_file_size != nullptr) *out_file_size = g_fonts[i].size;
            void* data = g_fonts[i].data;
            pthread_mutex_unlock(&g_lock);
            return data;
        }
    }
    void* data = MAP_FAILED;
    size_t size = 0;
    if (g_fontCount < (int)(sizeof g_fonts / sizeof g_fonts[0]))
    {
        int fd = open(filename, O_RDONLY | O_CLOEXEC);
        struct stat st;
        if (fd >= 0 && fstat(fd, &st) == 0 && st.st_size > 0)
        {
            size = (size_t)st.st_size;
            data = mmap(nullptr, size, PROT_READ, MAP_PRIVATE, fd, 0);
        }
        if (fd >= 0) close(fd);
    }
    if (data == MAP_FAILED)
    {
        pthread_mutex_unlock(&g_lock);
        return ImFileLoadToMemory(filename, mode, out_file_size, padding_bytes);
    }
    MappedFont* font = &g_fonts[g_fontCount];
    strcpy(font->path, filename);
    font->data = (char*)data;
    font->size = size;
    __atomic_store_n(&g_fontCount, g_fontCount + 1, __ATOMIC_RELEASE);
    pthread_mutex_unlock(&g_lock);
    fprintf(stderr, "[imgui] font file %s mapped (%.1f MiB)\n", filename, size / 1048576.0);
    if (out_file_size != nullptr) *out_file_size = size;
    return data;
}
