// libvulkan.dylib for the iOS host: sits between the engine and MoltenVK, as the Android host's
// libvulkan.so sits between the engine and the system's loader (host/vulkan/vulkan_interpose.c there).
// The engine binds it by name (NativeLibrary.Load("vulkan") -> libvulkan.dylib) and so does the GLFW
// shim (dlopen by its full path). Everything it forwards to MoltenVK.framework/MoltenVK, which it
// finds next to itself in the app bundle; the five global entry points are exported directly, every
// device-level function the engine takes through vkGetInstanceProcAddr, as Vulkan goes.
//
// M2's first version forwards untouched: the Android interposer's remedies are Android-driver ills
// (the rotated swapchain, Adreno's descriptor-pool memory, non-coherent host memory) that Metal does
// not have. The wrapper around vkGetInstanceProcAddr stays: it is the one door every function comes
// back through, the place a hook goes when a real iOS problem shows up. The port17_vulkan_* entries
// keep the names the GLFW shim calls (the contract docs/ENGINE-NATIVE-CONTRACT.md), as no-ops here.
#include <dlfcn.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdio.h>
#include <string.h>
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>

#define API __attribute__((visibility("default")))

static void (*g_log_sink)(const char*) = NULL;

API void port17_vulkan_set_log_sink(void (*sink)(const char*)) { g_log_sink = sink; }

static void log_line(const char* fmt, ...)
{
    char text[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(text, sizeof text, fmt, ap);
    va_end(ap);
    if (g_log_sink) g_log_sink(text);
    fprintf(stderr, "[vulkan] %s\n", text);
}

static void* g_moltenvk;                       // MoltenVK.framework/MoltenVK, next to this dylib
static pthread_once_t g_load_once = PTHREAD_ONCE_INIT;
static bool g_loaded;
static PFN_vkGetInstanceProcAddr g_real_gipa;
static PFN_vkCreateInstance g_real_create_instance;
static PFN_vkEnumerateInstanceExtensionProperties g_real_enum_ext;
static PFN_vkEnumerateInstanceLayerProperties g_real_enum_layers;
static PFN_vkEnumerateInstanceVersion g_real_enum_version;

static void load_moltenvk(void)
{
    // By full path through this dylib's own place: dlopen("libvulkan.dylib") would hand back ourselves.
    Dl_info self;
    char path[512] = "";
    if (dladdr((void*)&load_moltenvk, &self) && self.dli_fname)
    {
        snprintf(path, sizeof path, "%s", self.dli_fname);
        char* slash = strrchr(path, '/');
        if (slash) snprintf(slash + 1, sizeof path - (size_t)(slash + 1 - path), "MoltenVK.framework/MoltenVK");
        g_moltenvk = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    }
    if (!g_moltenvk) g_moltenvk = dlopen("@rpath/MoltenVK.framework/MoltenVK", RTLD_NOW | RTLD_LOCAL);
    if (!g_moltenvk)
    {
        log_line("MoltenVK.framework: %s", dlerror());
        return;
    }
    g_real_gipa = (PFN_vkGetInstanceProcAddr)dlsym(g_moltenvk, "vkGetInstanceProcAddr");
    g_real_create_instance = (PFN_vkCreateInstance)dlsym(g_moltenvk, "vkCreateInstance");
    g_real_enum_ext = (PFN_vkEnumerateInstanceExtensionProperties)dlsym(g_moltenvk, "vkEnumerateInstanceExtensionProperties");
    g_real_enum_layers = (PFN_vkEnumerateInstanceLayerProperties)dlsym(g_moltenvk, "vkEnumerateInstanceLayerProperties");
    g_real_enum_version = (PFN_vkEnumerateInstanceVersion)dlsym(g_moltenvk, "vkEnumerateInstanceVersion");
    if (!g_real_gipa)
    {
        log_line("MoltenVK.framework has no vkGetInstanceProcAddr");
        return;
    }
    g_loaded = true;
    if (path[0]) log_line("MoltenVK loaded from %s", path);
    uint32_t version = 0;
    if (g_real_enum_version)
    {
        g_real_enum_version(&version);
        log_line("MoltenVK instance version %u.%u.%u", VK_API_VERSION_MAJOR(version),
                 VK_API_VERSION_MINOR(version), VK_API_VERSION_PATCH(version));
    }
}

static bool ready(void)
{
    pthread_once(&g_load_once, load_moltenvk);
    return g_loaded;
}

// The hook point: every function name comes through here. M2 forwards as-is; a hook is a strcmp away.
API VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL vkGetInstanceProcAddr(VkInstance instance, const char* name)
{
    if (!ready()) return NULL;
    return g_real_gipa(instance, name);
}

API VKAPI_ATTR VkResult VKAPI_CALL vkCreateInstance(const VkInstanceCreateInfo* info, const VkAllocationCallbacks* alloc, VkInstance* out)
{
    if (!ready()) return VK_ERROR_INITIALIZATION_FAILED;
    if (info && info->pApplicationInfo)
        log_line("vkCreateInstance: %s (app %u.%u.%u, engine %s, wants API %u.%u)",
                 info->pApplicationInfo->pApplicationName ? info->pApplicationInfo->pApplicationName : "?",
                 VK_API_VERSION_MAJOR(info->pApplicationInfo->applicationVersion),
                 VK_API_VERSION_MINOR(info->pApplicationInfo->applicationVersion),
                 VK_API_VERSION_PATCH(info->pApplicationInfo->applicationVersion),
                 info->pApplicationInfo->pEngineName ? info->pApplicationInfo->pEngineName : "?",
                 VK_API_VERSION_MAJOR(info->pApplicationInfo->apiVersion),
                 VK_API_VERSION_MINOR(info->pApplicationInfo->apiVersion));
    else
        log_line("vkCreateInstance (no application info)");
    return g_real_create_instance(info, alloc, out);
}

API VKAPI_ATTR VkResult VKAPI_CALL vkEnumerateInstanceExtensionProperties(const char* layer, uint32_t* count, VkExtensionProperties* props)
{
    if (!ready()) return VK_ERROR_INITIALIZATION_FAILED;
    return g_real_enum_ext ? g_real_enum_ext(layer, count, props) : VK_ERROR_INITIALIZATION_FAILED;
}

API VKAPI_ATTR VkResult VKAPI_CALL vkEnumerateInstanceLayerProperties(uint32_t* count, VkLayerProperties* props)
{
    if (!ready()) return VK_ERROR_INITIALIZATION_FAILED;
    return g_real_enum_layers ? g_real_enum_layers(count, props) : VK_ERROR_INITIALIZATION_FAILED;
}

API VKAPI_ATTR VkResult VKAPI_CALL vkEnumerateInstanceVersion(uint32_t* version)
{
    if (!ready()) return VK_ERROR_INITIALIZATION_FAILED;
    if (!g_real_enum_version) { *version = VK_API_VERSION_1_0; return VK_SUCCESS; }
    return g_real_enum_version(version);
}

// The GLFW shim's coordination entries (dlsym'ed by it, the names of the Android interposer): on iOS
// the drawable size is the CAMetalLayer's and follows the render size on its own, so there is nothing
// to tell MoltenVK — the entries exist so the shim's calls resolve.
API void port17_vulkan_render_size(uint32_t width, uint32_t height)
{
    log_line("render size %ux%u (the layer's drawable size rules; nothing to do)", width, height);
}

API void port17_vulkan_window_changed(void) {}

API void port17_vulkan_release_surface(VkInstance instance, VkSurfaceKHR surface, const VkAllocationCallbacks* alloc)
{
    if (!ready()) return;
    PFN_vkDestroySurfaceKHR destroy = (PFN_vkDestroySurfaceKHR)g_real_gipa(instance, "vkDestroySurfaceKHR");
    if (destroy) destroy(instance, surface, alloc);
}
