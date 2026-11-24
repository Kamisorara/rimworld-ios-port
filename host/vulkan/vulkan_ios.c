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
#include <stdlib.h>
#include <string.h>
#include <time.h>
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include <vulkan/vulkan_metal.h>

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

// Timing samples use the usual measurement discipline: aggregate first, print every five
// seconds. MoltenVK deliberately moves the nextDrawable wait out of vkAcquireNextImageKHR,
// so these separate calls are the evidence for whether that wait lands in export, submit,
// or present.
#define TIMING_SAMPLES 512
#define RESULT_BUCKETS 7

static volatile uint32_t g_swapchain_images;
static bool g_force_engine_swapchain;

typedef struct Metric
{
    const char* name;
    pthread_mutex_t lock;
    uint32_t head;
    uint32_t count;
    double samples[TIMING_SAMPLES];
    uint64_t calls;
    uint64_t results[RESULT_BUCKETS];
} Metric;

static Metric g_wait_fences = { "waitFences", PTHREAD_MUTEX_INITIALIZER, 0, 0, {0}, 0, {0} };
static Metric g_acquire = { "acquireImage", PTHREAD_MUTEX_INITIALIZER, 0, 0, {0}, 0, {0} };
static Metric g_export_metal = { "exportDrawable", PTHREAD_MUTEX_INITIALIZER, 0, 0, {0}, 0, {0} };
static Metric g_queue_submit = { "queueSubmit", PTHREAD_MUTEX_INITIALIZER, 0, 0, {0}, 0, {0} };
static Metric g_queue_present = { "queuePresent", PTHREAD_MUTEX_INITIALIZER, 0, 0, {0}, 0, {0} };
static Metric g_create_swapchain = { "createSwapchain", PTHREAD_MUTEX_INITIALIZER, 0, 0, {0}, 0, {0} };
static Metric g_get_swapchain_images = { "getSwapchainImages", PTHREAD_MUTEX_INITIALIZER, 0, 0, {0}, 0, {0} };

static pthread_mutex_t g_report_lock = PTHREAD_MUTEX_INITIALIZER;
static struct timespec g_report_started;
static bool g_report_started_valid;

static double timespec_ms(const struct timespec* value)
{
    return (double)value->tv_sec * 1000.0 + (double)value->tv_nsec / 1000000.0;
}

static double elapsed_ms(const struct timespec* start, const struct timespec* end)
{
    return timespec_ms(end) - timespec_ms(start);
}

static uint32_t result_bucket(VkResult result)
{
    switch (result)
    {
        case VK_SUCCESS: return 0;
        case VK_NOT_READY: return 1;
        case VK_TIMEOUT: return 2;
        case VK_INCOMPLETE: return 3;
        case VK_SUBOPTIMAL_KHR: return 4;
        case VK_ERROR_OUT_OF_DATE_KHR: return 5;
        default: return 6;
    }
}

static int metric_compare(const void* left, const void* right)
{
    double a = *(const double*)left;
    double b = *(const double*)right;
    return a < b ? -1 : (a > b ? 1 : 0);
}

static void metric_add(Metric* metric, double milliseconds, VkResult result)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    pthread_mutex_lock(&metric->lock);
    metric->samples[metric->head] = milliseconds;
    metric->head = (metric->head + 1) % TIMING_SAMPLES;
    if (metric->count < TIMING_SAMPLES) metric->count++;
    metric->calls++;
    metric->results[result_bucket(result)]++;
    pthread_mutex_unlock(&metric->lock);

    pthread_mutex_lock(&g_report_lock);
    if (!g_report_started_valid)
    {
        g_report_started = now;
        g_report_started_valid = true;
    }
    bool due = timespec_ms(&now) - timespec_ms(&g_report_started) >= 5000.0;
    if (due) g_report_started = now;
    pthread_mutex_unlock(&g_report_lock);
    if (due)
    {
        Metric* metrics[] = {
            &g_create_swapchain, &g_get_swapchain_images, &g_wait_fences, &g_acquire,
            &g_export_metal, &g_queue_submit, &g_queue_present,
        };
        static const char* resultNames[RESULT_BUCKETS] = {
            "success", "notReady", "timeout", "incomplete", "suboptimal", "outOfDate", "other",
        };
        log_line("frame timing window");
        for (size_t i = 0; i < sizeof(metrics) / sizeof(metrics[0]); i++)
        {
            Metric* m = metrics[i];
            double sorted[TIMING_SAMPLES];
            uint32_t count;
            uint64_t calls;
            uint64_t results[RESULT_BUCKETS];
            pthread_mutex_lock(&m->lock);
            count = m->count;
            calls = m->calls;
            memcpy(results, m->results, sizeof results);
            for (uint32_t j = 0; j < count; j++)
                sorted[j] = m->samples[(m->head + TIMING_SAMPLES - count + j) % TIMING_SAMPLES];
            m->head = 0;
            m->count = 0;
            memset(m->results, 0, sizeof m->results);
            pthread_mutex_unlock(&m->lock);
            if (count == 0) continue;
            qsort(sorted, count, sizeof sorted[0], metric_compare);
            uint32_t high = count > 1 ? (uint32_t)((count * 99 + 99) / 100) : 0;
            if (high >= count) high = count - 1;
            char resultText[160];
            size_t at = 0;
            resultText[0] = 0;
            for (uint32_t bucket = 0; bucket < RESULT_BUCKETS; bucket++)
            {
                if (results[bucket] == 0) continue;
                at += (size_t)snprintf(resultText + at, sizeof resultText - at, "%s%s=%llu",
                                       at ? " " : "", resultNames[bucket],
                                       (unsigned long long)results[bucket]);
            }
            log_line("timing %s: n=%llu median=%.2f 99%%=%.2f max=%.2f ms; %s",
                     m->name, (unsigned long long)calls, sorted[count / 2],
                     sorted[high], sorted[count - 1], resultText);
        }
        log_line("swapchain images observed=%u", (unsigned)g_swapchain_images);
    }
}

static int swapchain_image_request(void)
{
    const char* value = getenv("PORT17_SWAPCHAIN_IMAGES");
    if (!value || !*value) return g_force_engine_swapchain ? 3 : 0;
    return atoi(value);
}

static void* g_moltenvk;                       // MoltenVK.framework/MoltenVK, next to this dylib
static pthread_once_t g_load_once = PTHREAD_ONCE_INIT;
static bool g_loaded;
static PFN_vkGetInstanceProcAddr g_real_gipa;
static PFN_vkCreateInstance g_real_create_instance;
static PFN_vkEnumerateInstanceExtensionProperties g_real_enum_ext;
static PFN_vkEnumerateInstanceLayerProperties g_real_enum_layers;
static PFN_vkEnumerateInstanceVersion g_real_enum_version;
static PFN_vkGetDeviceProcAddr g_real_gdpa;
static PFN_vkExportMetalObjectsEXT g_real_export_metal;
static PFN_vkQueueSubmit g_real_queue_submit;
static PFN_vkQueuePresentKHR g_real_queue_present;
static PFN_vkAcquireNextImageKHR g_real_acquire;
static PFN_vkWaitForFences g_real_wait_fences;
static PFN_vkCreateSwapchainKHR g_real_create_swapchain;
static PFN_vkGetSwapchainImagesKHR g_real_get_swapchain_images;
static PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR g_real_surface_caps;
static PFN_vkGetPhysicalDeviceSurfacePresentModesKHR g_real_surface_modes;

static bool g_caps_valid;
static VkSurfaceKHR g_caps_surface;
static VkSurfaceCapabilitiesKHR g_caps;

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

static void log_surface_caps(VkPhysicalDevice physical, VkSurfaceKHR surface,
                             const VkSurfaceCapabilitiesKHR* caps)
{
    log_line("surface caps: physical=%p surface=%p minImages=%u maxImages=%u extent=%ux%u usage=0x%x",
             (void*)physical, (void*)surface, caps->minImageCount, caps->maxImageCount,
             caps->currentExtent.width, caps->currentExtent.height,
             (unsigned)caps->supportedUsageFlags);
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_vkGetPhysicalDeviceSurfaceCapabilitiesKHR(
    VkPhysicalDevice physical, VkSurfaceKHR surface, VkSurfaceCapabilitiesKHR* caps)
{
    if (!g_real_surface_caps) return VK_ERROR_INITIALIZATION_FAILED;
    VkResult result = g_real_surface_caps(physical, surface, caps);
    if (result == VK_SUCCESS)
    {
        g_caps = *caps;
        g_caps_surface = surface;
        g_caps_valid = true;
        log_surface_caps(physical, surface, caps);
    }
    else
    {
        log_line("vkGetPhysicalDeviceSurfaceCapabilitiesKHR failed VkResult=%d", (int)result);
    }
    return result;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_vkGetPhysicalDeviceSurfacePresentModesKHR(
    VkPhysicalDevice physical, VkSurfaceKHR surface, uint32_t* count, VkPresentModeKHR* modes)
{
    if (!g_real_surface_modes) return VK_ERROR_INITIALIZATION_FAILED;
    VkResult result = g_real_surface_modes(physical, surface, count, modes);
    if (result == VK_SUCCESS && modes && count)
    {
        uint32_t shown = *count < 8 ? *count : 8;
        log_line("surface present modes: n=%u", *count);
        for (uint32_t i = 0; i < shown; i++)
            log_line("surface present mode [%u]=%d", i, (int)modes[i]);
    }
    (void)physical;
    (void)surface;
    return result;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_vkWaitForFences(
    VkDevice device, uint32_t fenceCount, const VkFence* fences, VkBool32 waitAll, uint64_t timeout)
{
    struct timespec start;
    clock_gettime(CLOCK_MONOTONIC, &start);
    VkResult result = g_real_wait_fences(device, fenceCount, fences, waitAll, timeout);
    struct timespec end;
    clock_gettime(CLOCK_MONOTONIC, &end);
    metric_add(&g_wait_fences, elapsed_ms(&start, &end), result);
    return result;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_vkAcquireNextImageKHR(
    VkDevice device, VkSwapchainKHR swapchain, uint64_t timeout,
    VkSemaphore semaphore, VkFence fence, uint32_t* imageIndex)
{
    struct timespec start;
    clock_gettime(CLOCK_MONOTONIC, &start);
    VkResult result = g_real_acquire(device, swapchain, timeout, semaphore, fence, imageIndex);
    struct timespec end;
    clock_gettime(CLOCK_MONOTONIC, &end);
    metric_add(&g_acquire, elapsed_ms(&start, &end), result);
    return result;
}

static VKAPI_ATTR void VKAPI_CALL hook_vkExportMetalObjectsEXT(
    VkDevice device, VkExportMetalObjectsInfoEXT* info)
{
    struct timespec start;
    clock_gettime(CLOCK_MONOTONIC, &start);
    g_real_export_metal(device, info);
    struct timespec end;
    clock_gettime(CLOCK_MONOTONIC, &end);
    metric_add(&g_export_metal, elapsed_ms(&start, &end), VK_SUCCESS);
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_vkQueueSubmit(
    VkQueue queue, uint32_t submitCount, const VkSubmitInfo* submits, VkFence fence)
{
    struct timespec start;
    clock_gettime(CLOCK_MONOTONIC, &start);
    VkResult result = g_real_queue_submit(queue, submitCount, submits, fence);
    struct timespec end;
    clock_gettime(CLOCK_MONOTONIC, &end);
    metric_add(&g_queue_submit, elapsed_ms(&start, &end), result);
    return result;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_vkQueuePresentKHR(
    VkQueue queue, const VkPresentInfoKHR* info)
{
    struct timespec start;
    clock_gettime(CLOCK_MONOTONIC, &start);
    VkResult result = g_real_queue_present(queue, info);
    struct timespec end;
    clock_gettime(CLOCK_MONOTONIC, &end);
    metric_add(&g_queue_present, elapsed_ms(&start, &end), result);
    return result;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_vkCreateSwapchainKHR(
    VkDevice device, const VkSwapchainCreateInfoKHR* info,
    const VkAllocationCallbacks* alloc, VkSwapchainKHR* swapchain)
{
    VkSwapchainCreateInfoKHR copy = *info;
    int requested = swapchain_image_request();
    bool forced = false;
    bool caps_allow = g_caps_valid && g_caps_surface == info->surface &&
                      (g_caps.maxImageCount == 0 ||
                       g_caps.maxImageCount >= (uint32_t)requested);
    if (requested > 0 && caps_allow && copy.minImageCount < (uint32_t)requested)
    {
        copy.minImageCount = (uint32_t)requested;
        forced = true;
    }
    if (requested > 0 && !forced && copy.minImageCount < (uint32_t)requested)
        log_line("vkCreateSwapchainKHR cannot force %d images (capsValid=%d capsSurface=%p)",
                 requested, g_caps_valid, (void*)g_caps_surface);
    log_line("vkCreateSwapchainKHR: min=%u request=%d%s extent=%ux%u format=%d mode=%d "
             "usage=0x%x alpha=%d transform=%d clipped=%d oldSwapchain=%p capsMax=%u",
             info->minImageCount, requested, forced ? " forced" : "",
             info->imageExtent.width, info->imageExtent.height,
             (int)info->imageFormat, (int)info->presentMode,
             (unsigned)info->imageUsage, (int)info->compositeAlpha,
             (int)info->preTransform, (int)info->clipped,
             (void*)info->oldSwapchain, g_caps_valid ? g_caps.maxImageCount : 0);
    struct timespec start;
    clock_gettime(CLOCK_MONOTONIC, &start);
    VkResult result = g_real_create_swapchain(device, &copy, alloc, swapchain);
    struct timespec end;
    clock_gettime(CLOCK_MONOTONIC, &end);
    metric_add(&g_create_swapchain, elapsed_ms(&start, &end), result);
    return result;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_vkGetSwapchainImagesKHR(
    VkDevice device, VkSwapchainKHR swapchain, uint32_t* count, VkImage* images)
{
    VkResult result = g_real_get_swapchain_images(device, swapchain, count, images);
    if (result == VK_SUCCESS && count && (images == NULL || result == VK_SUCCESS))
        g_swapchain_images = *count;
    metric_add(&g_get_swapchain_images, 0.0, result);
    if (images) log_line("vkGetSwapchainImagesKHR returned=%u", *count);
    return result;
}

static PFN_vkVoidFunction hook_instance_function(VkInstance instance, const char* name)
{
#define INSTANCE_HOOK(type, variable, symbol) \
    if (!strcmp(name, #symbol)) \
    { \
        variable = (type)g_real_gipa(instance, name); \
        return (PFN_vkVoidFunction)hook_##symbol; \
    }
    INSTANCE_HOOK(PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR,
                  g_real_surface_caps, vkGetPhysicalDeviceSurfaceCapabilitiesKHR)
    INSTANCE_HOOK(PFN_vkGetPhysicalDeviceSurfacePresentModesKHR,
                  g_real_surface_modes, vkGetPhysicalDeviceSurfacePresentModesKHR)
    INSTANCE_HOOK(PFN_vkQueueSubmit, g_real_queue_submit, vkQueueSubmit)
    INSTANCE_HOOK(PFN_vkQueuePresentKHR, g_real_queue_present, vkQueuePresentKHR)
    INSTANCE_HOOK(PFN_vkAcquireNextImageKHR, g_real_acquire, vkAcquireNextImageKHR)
    INSTANCE_HOOK(PFN_vkWaitForFences, g_real_wait_fences, vkWaitForFences)
    INSTANCE_HOOK(PFN_vkCreateSwapchainKHR, g_real_create_swapchain, vkCreateSwapchainKHR)
    INSTANCE_HOOK(PFN_vkGetSwapchainImagesKHR,
                  g_real_get_swapchain_images, vkGetSwapchainImagesKHR)
#undef INSTANCE_HOOK
    return g_real_gipa(instance, name);
}

static PFN_vkVoidFunction hook_device_function(VkDevice device, const char* name)
{
    if (!g_real_gdpa) return NULL;
#define DEVICE_HOOK(type, variable, symbol) \
    if (!strcmp(name, #symbol)) \
    { \
        variable = (type)g_real_gdpa(device, name); \
        return (PFN_vkVoidFunction)hook_##symbol; \
    }
    DEVICE_HOOK(PFN_vkExportMetalObjectsEXT, g_real_export_metal, vkExportMetalObjectsEXT)
    DEVICE_HOOK(PFN_vkQueueSubmit, g_real_queue_submit, vkQueueSubmit)
    DEVICE_HOOK(PFN_vkQueuePresentKHR, g_real_queue_present, vkQueuePresentKHR)
    DEVICE_HOOK(PFN_vkAcquireNextImageKHR, g_real_acquire, vkAcquireNextImageKHR)
    DEVICE_HOOK(PFN_vkWaitForFences, g_real_wait_fences, vkWaitForFences)
    DEVICE_HOOK(PFN_vkCreateSwapchainKHR, g_real_create_swapchain, vkCreateSwapchainKHR)
    DEVICE_HOOK(PFN_vkGetSwapchainImagesKHR,
                g_real_get_swapchain_images, vkGetSwapchainImagesKHR)
#undef DEVICE_HOOK
    return g_real_gdpa(device, name);
}

// The hook point: every function name comes through here. M2 forwards as-is; a hook is a strcmp away.
API VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL vkGetInstanceProcAddr(VkInstance instance, const char* name)
{
    if (!ready()) return NULL;
    if (name && !strcmp(name, "vkGetDeviceProcAddr"))
    {
        g_real_gdpa = (PFN_vkGetDeviceProcAddr)g_real_gipa(instance, name);
        return (PFN_vkVoidFunction)hook_device_function;
    }
    if (name) return hook_instance_function(instance, name);
    return g_real_gipa(instance, name);
}

API VKAPI_ATTR VkResult VKAPI_CALL vkCreateInstance(const VkInstanceCreateInfo* info, const VkAllocationCallbacks* alloc, VkInstance* out)
{
    if (!ready()) return VK_ERROR_INITIALIZATION_FAILED;
    if (info && info->pApplicationInfo)
    {
        log_line("vkCreateInstance: %s (app %u.%u.%u, engine %s, wants API %u.%u)",
                 info->pApplicationInfo->pApplicationName ? info->pApplicationInfo->pApplicationName : "?",
                 VK_API_VERSION_MAJOR(info->pApplicationInfo->applicationVersion),
                 VK_API_VERSION_MINOR(info->pApplicationInfo->applicationVersion),
                 VK_API_VERSION_PATCH(info->pApplicationInfo->applicationVersion),
                 info->pApplicationInfo->pEngineName ? info->pApplicationInfo->pEngineName : "?",
                 VK_API_VERSION_MAJOR(info->pApplicationInfo->apiVersion),
                 VK_API_VERSION_MINOR(info->pApplicationInfo->apiVersion));
        g_force_engine_swapchain = !(info->pApplicationInfo->pApplicationName &&
                                     strstr(info->pApplicationInfo->pApplicationName, "port17-triangle"));
        log_line("swapchain default: %s", g_force_engine_swapchain ? "3 images" : "off (triangle)");
    }
    else
    {
        log_line("vkCreateInstance (no application info)");
    }
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
