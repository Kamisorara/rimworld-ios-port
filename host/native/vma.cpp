// libvma.so: the Vulkan Memory Allocator with its C API exported, as the engine's Brutal.Vulkan.Vma expects
// (vma.dll on Windows). The engine creates the allocator without passing Vulkan function pointers, so the
// exported vmaCreateAllocator fills them in from the bundle's libvulkan.dylib (the forwarder over MoltenVK) before calling the real one.
//
// Images of some size get memory of their own (vmaCreateImage below). The engine packs textures into blocks of
// 64 MiB, and when its texture budget is passed it lets go of the textures that were never drawn; on a desktop card
// that only has to make room inside the blocks. On a phone the blocks are ordinary memory, and a block is given back
// only when the last texture in it has gone: with 106 mods (12 000 textures loaded, 9 000 let go again) the blocks
// held 3.65 GiB for 2.24 GiB of textures, targets and buffers. With memory of its own a texture gives back exactly
// what it took. PORT17_VMA_DEDICATED_KB in the environment sets the size from which that is done (128; 0: never).
#include <dlfcn.h>
#include <string.h>
#include <stdarg.h>
#include <stdio.h>
// The host installs its log sink (screen + Documents) before anything else.
static void (*g_vma_log_sink)(const char*) = nullptr;
extern "C" __attribute__((visibility("default"))) void port17_vma_set_log_sink(void (*sink)(const char*)) { g_vma_log_sink = sink; }
static void vma_log(const char* fmt, ...) __attribute__((format(printf, 1, 2)));
static void vma_log(const char* fmt, ...)
{
    char text[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(text, sizeof text, fmt, ap);
    va_end(ap);
    if (g_vma_log_sink) g_vma_log_sink(text);
    else fprintf(stderr, "[vma] %s
", text);
}
#define VK_NO_PROTOTYPES
#define VMA_IMPLEMENTATION
#define VMA_STATIC_VULKAN_FUNCTIONS 0
#define VMA_DYNAMIC_VULKAN_FUNCTIONS 1
#define VMA_VULKAN_VERSION 1003000
#include <stdlib.h>
#define vmaCreateAllocator vmaCreateAllocatorImpl
#define vmaCreateImage vmaCreateImageImpl
#include "vk_mem_alloc.h"
#undef vmaCreateAllocator
#undef vmaCreateImage

static VkDeviceSize g_dedicatedFrom = 128 * 1024;
static int g_dedicatedRead;

// About what an image takes, from its size and format (block-compressed formats by their block size).
static VkDeviceSize approximate_bytes(const VkImageCreateInfo* info)
{
    double perPixel;
    VkFormat f = info->format;
    if ((f >= VK_FORMAT_BC1_RGB_UNORM_BLOCK && f <= VK_FORMAT_BC1_RGBA_SRGB_BLOCK) || f == VK_FORMAT_BC4_UNORM_BLOCK || f == VK_FORMAT_BC4_SNORM_BLOCK) perPixel = 0.5;
    else if (f >= VK_FORMAT_BC2_UNORM_BLOCK && f <= VK_FORMAT_BC7_SRGB_BLOCK) perPixel = 1;
    else if (f >= VK_FORMAT_R8_UNORM && f <= VK_FORMAT_R8_SRGB) perPixel = 1;
    else if ((f >= VK_FORMAT_R8G8_UNORM && f <= VK_FORMAT_R8G8_SRGB) || f == VK_FORMAT_D16_UNORM || (f >= VK_FORMAT_R16_UNORM && f <= VK_FORMAT_R16_SFLOAT)) perPixel = 2;
    else if (f >= VK_FORMAT_R16G16B16A16_UNORM && f <= VK_FORMAT_R16G16B16A16_SFLOAT) perPixel = 8;
    else if (f >= VK_FORMAT_R32G32B32A32_UINT && f <= VK_FORMAT_R32G32B32A32_SFLOAT) perPixel = 16;
    else if (f == VK_FORMAT_D32_SFLOAT_S8_UINT) perPixel = 5;
    else perPixel = 4;
    double bytes = perPixel * info->extent.width * info->extent.height * (info->extent.depth ? info->extent.depth : 1) * (info->arrayLayers ? info->arrayLayers : 1) * (double)info->samples;
    if (info->mipLevels > 1) bytes *= 4.0 / 3.0;
    return (VkDeviceSize)bytes;
}

extern "C" VkResult vmaCreateImage(VmaAllocator allocator, const VkImageCreateInfo* pImageCreateInfo, const VmaAllocationCreateInfo* pAllocationCreateInfo, VkImage* pImage, VmaAllocation* pAllocation, VmaAllocationInfo* pAllocationInfo)
{
    if (!g_dedicatedRead)
    {
        const char* e = getenv("PORT17_VMA_DEDICATED_KB");
        if (e != nullptr && *e != 0) g_dedicatedFrom = (VkDeviceSize)atoll(e) * 1024;
        g_dedicatedRead = 1;
        vma_log("vma: images of %llu KiB and more get memory of their own", (unsigned long long)(g_dedicatedFrom / 1024));
    }
    if (g_dedicatedFrom == 0 || pImageCreateInfo == nullptr || pAllocationCreateInfo == nullptr || pAllocationCreateInfo->pool != VK_NULL_HANDLE
        || (pAllocationCreateInfo->flags & (VMA_ALLOCATION_CREATE_NEVER_ALLOCATE_BIT | VMA_ALLOCATION_CREATE_DEDICATED_MEMORY_BIT)) != 0 || approximate_bytes(pImageCreateInfo) < g_dedicatedFrom)
    {
        return vmaCreateImageImpl(allocator, pImageCreateInfo, pAllocationCreateInfo, pImage, pAllocation, pAllocationInfo);
    }
    VmaAllocationCreateInfo own = *pAllocationCreateInfo;
    own.flags |= VMA_ALLOCATION_CREATE_DEDICATED_MEMORY_BIT;
    VkResult r = vmaCreateImageImpl(allocator, pImageCreateInfo, &own, pImage, pAllocation, pAllocationInfo);
    if (r != VK_SUCCESS)
    {
        // As the engine asked, then: its own handling of a refusal (letting textures go and trying again) sees the same answer it always did.
        r = vmaCreateImageImpl(allocator, pImageCreateInfo, pAllocationCreateInfo, pImage, pAllocation, pAllocationInfo);
    }
    return r;
}

extern "C" VkResult vmaCreateAllocator(const VmaAllocatorCreateInfo* pCreateInfo, VmaAllocator* pAllocator)
{
    VmaAllocatorCreateInfo info = *pCreateInfo;
    VmaVulkanFunctions functions = {};
    if (info.pVulkanFunctions != nullptr)
    {
        functions = *info.pVulkanFunctions;
    }
    if (functions.vkGetInstanceProcAddr == nullptr)
    {
        // The libvulkan.dylib beside this library is the port's forwarder (host/vulkan), which stands before MoltenVK; the
        // engine cannot use and counts allocations; by name alone the linker may answer with the system's loader,
        // which the interposer has opened too. So it is asked for by its path.
        static void* vulkan = nullptr;
        Dl_info self;
        if (vulkan == nullptr && dladdr((void*)&vmaCreateAllocator, &self) != 0 && self.dli_fname != nullptr)
        {
            char path[1024];
            const char* slash = strrchr(self.dli_fname, '/');
            if (slash != nullptr && (size_t)(slash - self.dli_fname) + 16 < sizeof path)
            {
                memcpy(path, self.dli_fname, slash - self.dli_fname + 1);
                strcpy(path + (slash - self.dli_fname) + 1, "libvulkan.dylib");
                vulkan = dlopen(path, RTLD_NOW | RTLD_LOCAL);
            }
        }
        if (vulkan == nullptr)
        {
            vulkan = dlopen("libvulkan.dylib", RTLD_NOW | RTLD_LOCAL);
        }
        vma_log("vma: Vulkan functions come from %s", vulkan == nullptr ? "nowhere" : dlsym(vulkan, "port17_vulkan_window_changed") != nullptr ? "the port's forwarder" : "another loader");
        if (vulkan != nullptr)
        {
            functions.vkGetInstanceProcAddr = (PFN_vkGetInstanceProcAddr)dlsym(vulkan, "vkGetInstanceProcAddr");
        }
    }
    if (functions.vkGetInstanceProcAddr == nullptr)
    {
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    if (functions.vkGetDeviceProcAddr == nullptr)
    {
        functions.vkGetDeviceProcAddr = (PFN_vkGetDeviceProcAddr)functions.vkGetInstanceProcAddr(info.instance, "vkGetDeviceProcAddr");
    }
    info.pVulkanFunctions = &functions;
    return vmaCreateAllocatorImpl(&info, pAllocator);
}
