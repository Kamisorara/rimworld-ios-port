// The M2 graphics-stack proof: a Vulkan triangle drawn through the very path the engine will take —
// libglfw.dylib (the iOS shim) for the window and the surface, libvulkan.dylib (the forwarder) for
// every function (through glfwGetInstanceProcAddress, as Brutal.Vulkan goes), MoltenVK under it, the
// CAMetalLayer under that. No CoreCLR, no game: what it proves is the native half of M2.
//
// It also exercises the input paths: raw finger events (glfwAndroidPollTouches) drag the triangle's
// first vertex, the char callback types into the log, the key callback stops on Escape / the window
// going away. Runs on its own pthread (host/app/main.c), renders until the app dies; the window's 0x0
// (the app in the background) pauses drawing, as the engine treats it.
#include <mach-o/dyld.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

#define GLFW_RELEASE 0
#define GLFW_PRESS 1
typedef struct GLFWwindow GLFWwindow;
typedef void (*GLFWcharfun)(GLFWwindow*, unsigned int);
typedef void (*GLFWkeyfun)(GLFWwindow*, int, int, int, int);

// The shim's GLFW entries this proof uses (libglfw.dylib is linked).
extern int glfwInit(void);
extern GLFWwindow* glfwCreateWindow(int width, int height, const char* title, void* monitor, void* share);
extern void glfwPollEvents(void);
extern void glfwGetFramebufferSize(GLFWwindow* w, int* width, int* height);
extern int glfwWindowShouldClose(GLFWwindow* w);
extern void glfwSetWindowShouldClose(GLFWwindow* w, int value);
extern int glfwVulkanSupported(void);
extern const char** glfwGetRequiredInstanceExtensions(uint32_t* count);
extern void* glfwGetInstanceProcAddress(void* instance, const char* name);
extern int glfwCreateWindowSurface(void* instance, GLFWwindow* window, const void* allocator, void* surface);
extern GLFWcharfun glfwSetCharCallback(GLFWwindow* w, GLFWcharfun cb);
extern GLFWkeyfun glfwSetKeyCallback(GLFWwindow* w, GLFWkeyfun cb);
extern int glfwGetError(const char** description);
// The raw touch queue (the managed layer's path), the Android name.
#include "../glfw/glfw_ios.h"

// The host's log (screen + Documents file), installed by main.m before the thread starts.
void host_log(const char* msg);

static void logf_(const char* fmt, ...)
{
    char text[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(text, sizeof text, fmt, ap);
    va_end(ap);
    host_log(text);
}

// The engine's way in: every Vulkan function through vkGetInstanceProcAddr over "vulkan".
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#define VK_DEFINE_GIPA(call) static PFN_##call call;
VK_DEFINE_GIPA(vkCreateInstance)
VK_DEFINE_GIPA(vkEnumerateInstanceExtensionProperties)
VK_DEFINE_GIPA(vkDestroyInstance)
static PFN_vkGetInstanceProcAddr gipa;
static VkInstance g_instance;

#define VK_GET(instance, call) call = (PFN_##call)gipa(instance, #call);
static PFN_vkDestroySurfaceKHR vkDestroySurfaceKHR;
static PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR vkGetPhysicalDeviceSurfaceCapabilitiesKHR;
static PFN_vkGetPhysicalDeviceSurfaceFormatsKHR vkGetPhysicalDeviceSurfaceFormatsKHR;
static PFN_vkGetPhysicalDeviceSurfacePresentModesKHR vkGetPhysicalDeviceSurfacePresentModesKHR;
static PFN_vkGetPhysicalDeviceQueueFamilyProperties vkGetPhysicalDeviceQueueFamilyProperties;
static PFN_vkGetPhysicalDeviceProperties vkGetPhysicalDeviceProperties;
static PFN_vkCreateDevice vkCreateDevice;
static PFN_vkDestroyDevice vkDestroyDevice;
static PFN_vkDeviceWaitIdle vkDeviceWaitIdle;
static PFN_vkCreateSwapchainKHR vkCreateSwapchainKHR;
static PFN_vkDestroySwapchainKHR vkDestroySwapchainKHR;
static PFN_vkGetSwapchainImagesKHR vkGetSwapchainImagesKHR;
static PFN_vkAcquireNextImageKHR vkAcquireNextImageKHR;
static PFN_vkQueuePresentKHR vkQueuePresentKHR;
static PFN_vkCreateImageView vkCreateImageView;
static PFN_vkDestroyImageView vkDestroyImageView;
static PFN_vkCreateShaderModule vkCreateShaderModule;
static PFN_vkDestroyShaderModule vkDestroyShaderModule;
static PFN_vkCreateRenderPass vkCreateRenderPass;
static PFN_vkDestroyRenderPass vkDestroyRenderPass;
static PFN_vkCreateFramebuffer vkCreateFramebuffer;
static PFN_vkDestroyFramebuffer vkDestroyFramebuffer;
static PFN_vkCreatePipelineLayout vkCreatePipelineLayout;
static PFN_vkDestroyPipelineLayout vkDestroyPipelineLayout;
static PFN_vkCreateGraphicsPipelines vkCreateGraphicsPipelines;
static PFN_vkDestroyPipeline vkDestroyPipeline;
static PFN_vkCreateCommandPool vkCreateCommandPool;
static PFN_vkDestroyCommandPool vkDestroyCommandPool;
static PFN_vkAllocateCommandBuffers vkAllocateCommandBuffers;
static PFN_vkCreateSemaphore vkCreateSemaphore;
static PFN_vkDestroySemaphore vkDestroySemaphore;
static PFN_vkCreateFence vkCreateFence;
static PFN_vkDestroyFence vkDestroyFence;
static PFN_vkResetFences vkResetFences;
static PFN_vkWaitForFences vkWaitForFences;
static PFN_vkBeginCommandBuffer vkBeginCommandBuffer;
static PFN_vkEndCommandBuffer vkEndCommandBuffer;
static PFN_vkResetCommandBuffer vkResetCommandBuffer;
static PFN_vkQueueSubmit vkQueueSubmit;
static PFN_vkAllocateMemory vkAllocateMemory;
static PFN_vkFreeMemory vkFreeMemory;
static PFN_vkMapMemory vkMapMemory;
static PFN_vkUnmapMemory vkUnmapMemory;
static PFN_vkBindBufferMemory vkBindBufferMemory;
static PFN_vkCreateBuffer vkCreateBuffer;
static PFN_vkDestroyBuffer vkDestroyBuffer;
static PFN_vkCmdBeginRenderPass vkCmdBeginRenderPass;
static PFN_vkCmdEndRenderPass vkCmdEndRenderPass;
static PFN_vkCmdBindPipeline vkCmdBindPipeline;
static PFN_vkCmdSetViewport vkCmdSetViewport;
static PFN_vkCmdSetScissor vkCmdSetScissor;
static PFN_vkCmdBindVertexBuffers vkCmdBindVertexBuffers;
static PFN_vkCmdDraw vkCmdDraw;
static PFN_vkGetBufferMemoryRequirements vkGetBufferMemoryRequirements;
static PFN_vkGetPhysicalDeviceMemoryProperties vkGetPhysicalDeviceMemoryProperties;

// ---- state the input callbacks touch (the poll thread == this render thread) ----
static float g_dragX = 0.0f, g_dragY = -0.6f; // the dragged vertex's position (clip space)
static volatile int g_charCount, g_lastChar;
static volatile int g_stop;

static void on_char(GLFWwindow* w, unsigned int cp)
{
    (void)w;
    g_charCount++;
    g_lastChar = (int)cp;
}

static void on_key(GLFWwindow* w, int key, int scancode, int action, int mods)
{
    (void)w; (void)scancode; (void)mods;
    if (key == 256 && action == GLFW_PRESS) g_stop = 1; // Escape
}

static char* read_file(const char* name, long* size_out)
{
    char path[1024];
    uint32_t n = sizeof path - 1;
    _NSGetExecutablePath(path, &n);
    char* slash = strrchr(path, '/');
    if (slash) snprintf(slash + 1, sizeof path - (size_t)(slash + 1 - path), "%s", name);
    FILE* f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    rewind(f);
    char* data = malloc((size_t)size + 1);
    if (!data || fread(data, 1, (size_t)size, f) != (size_t)size) { free(data); fclose(f); return NULL; }
    fclose(f);
    *size_out = size;
    return data;
}

static char* read_shader(const char* name, long* size_out)
{
    // The .spv files the build puts in the bundle; beside them a base64 fallback is not worth it —
    // a missing shader is a build failure the log names.
    char file[128];
    snprintf(file, sizeof file, "%s.spv", name);
    char* data = read_file(file, size_out);
    if (!data) logf_("三角形：着色器 %s 打不开（bundle 里没有）", file);
    return data;
}

#define CHECK(expr, what) \
    do { VkResult r_ = (expr); if (r_ != VK_SUCCESS) { logf_("三角形：%s 失败 VkResult=%d", what, (int)r_); return false; } } while (0)

static bool create_instance(void)
{
    gipa = (PFN_vkGetInstanceProcAddr)glfwGetInstanceProcAddress(NULL, "vkGetInstanceProcAddr");
    vkCreateInstance = (PFN_vkCreateInstance)glfwGetInstanceProcAddress(NULL, "vkCreateInstance");
    vkEnumerateInstanceExtensionProperties = (PFN_vkEnumerateInstanceExtensionProperties)glfwGetInstanceProcAddress(NULL, "vkEnumerateInstanceExtensionProperties");
    if (!gipa || !vkCreateInstance) { logf_("三角形：vkGetInstanceProcAddr/vkCreateInstance 拿不到（libvulkan/MoltenVK 没起来）"); return false; }
    uint32_t needCount = 0;
    const char** need = glfwGetRequiredInstanceExtensions(&needCount);
    if (!need) { logf_("三角形：glfwGetRequiredInstanceExtensions 没给扩展"); return false; }
    for (uint32_t i = 0; i < needCount; i++) logf_("三角形：实例扩展 [%s]", need[i]);

    // What MoltenVK really offers (the port's own eyes, not the shim's word).
    uint32_t haveCount = 0;
    vkEnumerateInstanceExtensionProperties(NULL, &haveCount, NULL);
    VkExtensionProperties* have = calloc(haveCount ? haveCount : 1, sizeof *have);
    vkEnumerateInstanceExtensionProperties(NULL, &haveCount, have);
    int found = 0;
    for (uint32_t i = 0; i < haveCount; i++)
        for (uint32_t k = 0; k < needCount; k++)
            if (strcmp(have[i].extensionName, need[k]) == 0) found++;
    free(have);
    if (found != (int)needCount) { logf_("三角形：MoltenVK 只认了 %d/%d 个所需扩展", found, (int)needCount); return false; }

    VkApplicationInfo app = { VK_STRUCTURE_TYPE_APPLICATION_INFO };
    app.pApplicationName = "port17-triangle";
    app.applicationVersion = VK_MAKE_VERSION(0, 2, 0);
    app.pEngineName = "port17";
    app.apiVersion = VK_API_VERSION_1_1; // the engine's floor; MoltenVK 1.4.2 answers 1.4
    VkInstanceCreateInfo info = { VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO };
    info.pApplicationInfo = &app;
    info.enabledExtensionCount = needCount;
    info.ppEnabledExtensionNames = need;
    CHECK(vkCreateInstance(&info, NULL, &g_instance), "vkCreateInstance");
    return true;
}

typedef struct FrameSync { VkSemaphore acquire, render; VkFence fence; } FrameSync;
static VkSurfaceKHR g_surface;
static VkPhysicalDevice g_physical;
static uint32_t g_family;
static VkDevice g_device;
static VkQueue g_queue;
static VkSwapchainKHR g_swapchain;
static VkFormat g_format;
static uint32_t g_imageCount;
static VkImage* g_images;
static VkImageView* g_views;
static VkRenderPass g_renderPass;
static VkFramebuffer* g_framebuffers;
static VkPipelineLayout g_pipelineLayout;
static VkPipeline g_pipeline;
static VkCommandPool g_commandPool;
static VkCommandBuffer* g_commands;
static FrameSync* g_sync;
static uint32_t g_frameIndex;
static VkBuffer g_vertexBuffer;
static VkDeviceMemory g_vertexMemory;
static float* g_vertexMap; // 3 vertices * (x, y, r, g, b)
static VkExtent2D g_extent;

static bool pick_device_and_queue(void)
{
    VK_GET(g_instance, vkGetPhysicalDeviceSurfaceCapabilitiesKHR)
    VK_GET(g_instance, vkGetPhysicalDeviceSurfaceFormatsKHR)
    VK_GET(g_instance, vkGetPhysicalDeviceSurfacePresentModesKHR)
    VK_GET(g_instance, vkGetPhysicalDeviceQueueFamilyProperties)
    VK_GET(g_instance, vkGetPhysicalDeviceProperties)
    VK_GET(g_instance, vkCreateDevice)
    VK_GET(g_instance, vkDestroyDevice)
    VK_GET(g_instance, vkDestroySurfaceKHR)
    VK_GET(g_instance, vkDeviceWaitIdle)

    VkPhysicalDevice devices[8];
    uint32_t count = 8;
    PFN_vkEnumeratePhysicalDevices enumerate = (PFN_vkEnumeratePhysicalDevices)gipa(g_instance, "vkEnumeratePhysicalDevices");
    if (!enumerate || enumerate(g_instance, &count, devices) != VK_SUCCESS || count == 0)
    {
        logf_("三角形：枚举不到物理设备");
        return false;
    }
    g_physical = devices[0];
    VkPhysicalDeviceProperties props;
    vkGetPhysicalDeviceProperties(g_physical, &props);
    logf_("三角形：设备 %s（API %u.%u.%u）", props.deviceName,
          VK_API_VERSION_MAJOR(props.apiVersion), VK_API_VERSION_MINOR(props.apiVersion), VK_API_VERSION_PATCH(props.apiVersion));

    uint32_t families = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(g_physical, &families, NULL);
    VkQueueFamilyProperties familyProps[16];
    if (families > 16) families = 16;
    vkGetPhysicalDeviceQueueFamilyProperties(g_physical, &families, familyProps);
    g_family = UINT32_MAX;
    for (uint32_t i = 0; i < families; i++)
    {
        if ((familyProps[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) == 0) continue;
        VkBool32 present = VK_FALSE;
        PFN_vkGetPhysicalDeviceSurfaceSupportKHR support =
            (PFN_vkGetPhysicalDeviceSurfaceSupportKHR)gipa(g_instance, "vkGetPhysicalDeviceSurfaceSupportKHR");
        if (support) support(g_physical, i, g_surface, &present);
        if (present) { g_family = i; break; }
    }
    if (g_family == UINT32_MAX) { logf_("三角形：没有能画也能呈现的队列族"); return false; }
    logf_("三角形：队列族 %u", g_family);

    float priority = 1.0f;
    VkDeviceQueueCreateInfo queueInfo = { VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO };
    queueInfo.queueFamilyIndex = g_family;
    queueInfo.queueCount = 1;
    queueInfo.pQueuePriorities = &priority;
    VkDeviceCreateInfo info = { VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO };
    info.queueCreateInfoCount = 1;
    info.pQueueCreateInfos = &queueInfo;
    CHECK(vkCreateDevice(g_physical, &info, NULL, &g_device), "vkCreateDevice");
    // gipa 的第一参永远是实例句柄（MoltenVK 把它解引用成 MVKInstance*——上一版在这里塞了
    // VkDevice，设备对象按实例布局乱读，真机闪退；设备级函数也一样从实例取，指针本身通用）。
    PFN_vkGetDeviceQueue getQueue = (PFN_vkGetDeviceQueue)gipa(g_instance, "vkGetDeviceQueue");
    getQueue(g_device, g_family, 0, &g_queue);
    return true;
}

static bool create_swapchain(void)
{
    VK_GET(g_instance, vkCreateSwapchainKHR)
    VK_GET(g_instance, vkDestroySwapchainKHR)
    VK_GET(g_instance, vkGetSwapchainImagesKHR)
    VK_GET(g_instance, vkAcquireNextImageKHR)
    VK_GET(g_instance, vkQueuePresentKHR)

    VkSurfaceCapabilitiesKHR caps;
    CHECK(vkGetPhysicalDeviceSurfaceCapabilitiesKHR(g_physical, g_surface, &caps), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
    logf_("三角形：surface 当前尺寸 %ux%u，图像数 %u..%u", caps.currentExtent.width, caps.currentExtent.height,
          caps.minImageCount, caps.maxImageCount);

    uint32_t formats = 0;
    vkGetPhysicalDeviceSurfaceFormatsKHR(g_physical, g_surface, &formats, NULL);
    VkSurfaceFormatKHR formatList[16];
    if (formats > 16) formats = 16;
    vkGetPhysicalDeviceSurfaceFormatsKHR(g_physical, g_surface, &formats, formatList);
    g_format = formatList[0].format;
    VkColorSpaceKHR colorSpace = formatList[0].colorSpace;
    for (uint32_t i = 0; i < formats; i++)
    {
        if (formatList[i].format == VK_FORMAT_B8G8R8A8_UNORM || formatList[i].format == VK_FORMAT_B8G8R8A8_SRGB)
        {
            g_format = formatList[i].format;
            colorSpace = formatList[i].colorSpace;
            break;
        }
    }
    logf_("三角形：surface 格式 %d（色彩空间 %d），共 %u 种可选", (int)g_format, (int)colorSpace, formats);

    uint32_t modes = 0;
    vkGetPhysicalDeviceSurfacePresentModesKHR(g_physical, g_surface, &modes, NULL);
    VkPresentModeKHR modeList[8];
    if (modes > 8) modes = 8;
    vkGetPhysicalDeviceSurfacePresentModesKHR(g_physical, g_surface, &modes, modeList);
    VkPresentModeKHR mode = VK_PRESENT_MODE_FIFO_KHR; // always there, vsync — the engine's own choice on a phone
    for (uint32_t i = 0; i < modes; i++) if (modeList[i] == VK_PRESENT_MODE_MAILBOX_KHR) mode = VK_PRESENT_MODE_MAILBOX_KHR;
    for (uint32_t i = 0; i < modes; i++) logf_("三角形：呈现模式可选 %d%s", (int)modeList[i], modeList[i] == mode ? " ← 用这个" : "");

    g_extent = caps.currentExtent;
    g_imageCount = caps.minImageCount + 1;
    if (caps.maxImageCount > 0 && g_imageCount > caps.maxImageCount) g_imageCount = caps.maxImageCount;
    VkSwapchainCreateInfoKHR info = { VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR };
    info.surface = g_surface;
    info.minImageCount = g_imageCount;
    info.imageFormat = g_format;
    info.imageColorSpace = colorSpace;
    info.imageExtent = g_extent;
    info.imageArrayLayers = 1;
    info.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    info.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    info.preTransform = caps.currentTransform;
    info.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    info.presentMode = mode;
    info.clipped = VK_TRUE;
    CHECK(vkCreateSwapchainKHR(g_device, &info, NULL, &g_swapchain), "vkCreateSwapchainKHR");
    CHECK(vkGetSwapchainImagesKHR(g_device, g_swapchain, &g_imageCount, NULL), "vkGetSwapchainImagesKHR(数)");
    g_images = calloc(g_imageCount, sizeof *g_images);
    g_views = calloc(g_imageCount, sizeof *g_views);
    g_framebuffers = calloc(g_imageCount, sizeof *g_framebuffers);
    g_commands = calloc(g_imageCount, sizeof *g_commands);
    g_sync = calloc(g_imageCount, sizeof *g_sync);
    CHECK(vkGetSwapchainImagesKHR(g_device, g_swapchain, &g_imageCount, g_images), "vkGetSwapchainImagesKHR");
    return true;
}

static bool create_pipeline(void)
{
    VK_GET(g_instance, vkCreateImageView)
    VK_GET(g_instance, vkDestroyImageView)
    VK_GET(g_instance, vkCreateShaderModule)
    VK_GET(g_instance, vkDestroyShaderModule)
    VK_GET(g_instance, vkCreateRenderPass)
    VK_GET(g_instance, vkDestroyRenderPass)
    VK_GET(g_instance, vkCreateFramebuffer)
    VK_GET(g_instance, vkDestroyFramebuffer)
    VK_GET(g_instance, vkCreatePipelineLayout)
    VK_GET(g_instance, vkDestroyPipelineLayout)
    VK_GET(g_instance, vkCreateGraphicsPipelines)
    VK_GET(g_instance, vkDestroyPipeline)
    VK_GET(g_instance, vkCreateCommandPool)
    VK_GET(g_instance, vkDestroyCommandPool)
    VK_GET(g_instance, vkAllocateCommandBuffers)

    for (uint32_t i = 0; i < g_imageCount; i++)
    {
        VkImageViewCreateInfo view = { VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
        view.image = g_images[i];
        view.viewType = VK_IMAGE_VIEW_TYPE_2D;
        view.format = g_format;
        view.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        view.subresourceRange.levelCount = 1;
        view.subresourceRange.layerCount = 1;
        CHECK(vkCreateImageView(g_device, &view, NULL, &g_views[i]), "vkCreateImageView");
    }

    VkAttachmentDescription color = { 0 };
    color.format = g_format;
    color.samples = VK_SAMPLE_COUNT_1_BIT;
    color.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    color.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    color.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    color.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    color.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    color.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    VkAttachmentReference colorRef = { 0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL };
    VkSubpassDescription subpass = { 0 };
    subpass.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    subpass.colorAttachmentCount = 1;
    subpass.pColorAttachments = &colorRef;
    VkRenderPassCreateInfo passInfo = { VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO };
    passInfo.attachmentCount = 1;
    passInfo.pAttachments = &color;
    passInfo.subpassCount = 1;
    passInfo.pSubpasses = &subpass;
    CHECK(vkCreateRenderPass(g_device, &passInfo, NULL, &g_renderPass), "vkCreateRenderPass");

    for (uint32_t i = 0; i < g_imageCount; i++)
    {
        VkFramebufferCreateInfo fb = { VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO };
        fb.renderPass = g_renderPass;
        fb.attachmentCount = 1;
        fb.pAttachments = &g_views[i];
        fb.width = g_extent.width;
        fb.height = g_extent.height;
        fb.layers = 1;
        CHECK(vkCreateFramebuffer(g_device, &fb, NULL, &g_framebuffers[i]), "vkCreateFramebuffer");
    }

    long vertSize = 0, fragSize = 0;
    char* vert = read_shader("triangle.vert", &vertSize);
    char* frag = read_shader("triangle.frag", &fragSize);
    if (!vert || !frag) { free(vert); free(frag); return false; }
    VkShaderModuleCreateInfo vertInfo = { VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO };
    vertInfo.codeSize = (size_t)vertSize;
    vertInfo.pCode = (const uint32_t*)vert;
    VkShaderModuleCreateInfo fragInfo = { VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO };
    fragInfo.codeSize = (size_t)fragSize;
    fragInfo.pCode = (const uint32_t*)frag;
    VkShaderModule vertModule, fragModule;
    if (vkCreateShaderModule(g_device, &vertInfo, NULL, &vertModule) != VK_SUCCESS ||
        vkCreateShaderModule(g_device, &fragInfo, NULL, &fragModule) != VK_SUCCESS)
    {
        logf_("三角形：着色器模块创建失败（SPIR-V 损坏？）");
        free(vert); free(frag);
        return false;
    }
    free(vert);
    free(frag);

    VkPipelineShaderStageCreateInfo stages[2] = {
        { VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, NULL, 0, VK_SHADER_STAGE_VERTEX_BIT, vertModule, "main", NULL },
        { VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, NULL, 0, VK_SHADER_STAGE_FRAGMENT_BIT, fragModule, "main", NULL },
    };
    VkVertexInputBindingDescription binding = { 0, 5 * sizeof(float), VK_VERTEX_INPUT_RATE_VERTEX };
    VkVertexInputAttributeDescription attributes[2] = {
        { 0, 0, VK_FORMAT_R32G32_SFLOAT, 0 },             // position
        { 1, 0, VK_FORMAT_R32G32B32_SFLOAT, 2 * sizeof(float) }, // color
    };
    VkPipelineVertexInputStateCreateInfo vertexInput = { VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO };
    vertexInput.vertexBindingDescriptionCount = 1;
    vertexInput.pVertexBindingDescriptions = &binding;
    vertexInput.vertexAttributeDescriptionCount = 2;
    vertexInput.pVertexAttributeDescriptions = attributes;
    VkPipelineInputAssemblyStateCreateInfo inputAssembly = { VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO };
    inputAssembly.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    VkPipelineViewportStateCreateInfo viewportState = { VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO };
    viewportState.viewportCount = 1;
    viewportState.scissorCount = 1;
    VkPipelineRasterizationStateCreateInfo raster = { VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO };
    raster.polygonMode = VK_POLYGON_MODE_FILL;
    raster.lineWidth = 1.0f;
    raster.cullMode = VK_CULL_MODE_NONE;
    VkPipelineMultisampleStateCreateInfo multisample = { VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO };
    multisample.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
    VkPipelineColorBlendAttachmentState blend = { 0 };
    blend.colorWriteMask = 0xF;
    VkPipelineColorBlendStateCreateInfo colorBlend = { VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO };
    colorBlend.attachmentCount = 1;
    colorBlend.pAttachments = &blend;
    VkDynamicState dynamicStates[2] = { VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR };
    VkPipelineDynamicStateCreateInfo dynamic = { VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO };
    dynamic.dynamicStateCount = 2;
    dynamic.pDynamicStates = dynamicStates;
    CHECK(vkCreatePipelineLayout(g_device, &(VkPipelineLayoutCreateInfo){ VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO }, NULL, &g_pipelineLayout), "vkCreatePipelineLayout");
    VkGraphicsPipelineCreateInfo pipe = { VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO };
    pipe.stageCount = 2;
    pipe.pStages = stages;
    pipe.pVertexInputState = &vertexInput;
    pipe.pInputAssemblyState = &inputAssembly;
    pipe.pViewportState = &viewportState;
    pipe.pRasterizationState = &raster;
    pipe.pMultisampleState = &multisample;
    pipe.pColorBlendState = &colorBlend;
    pipe.pDynamicState = &dynamic;
    pipe.layout = g_pipelineLayout;
    pipe.renderPass = g_renderPass;
    pipe.subpass = 0;
    CHECK(vkCreateGraphicsPipelines(g_device, VK_NULL_HANDLE, 1, &pipe, NULL, &g_pipeline), "vkCreateGraphicsPipelines");
    vkDestroyShaderModule(g_device, vertModule, NULL);
    vkDestroyShaderModule(g_device, fragModule, NULL);

    VkCommandPoolCreateInfo poolInfo = { VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
    poolInfo.queueFamilyIndex = g_family;
    poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    CHECK(vkCreateCommandPool(g_device, &poolInfo, NULL, &g_commandPool), "vkCreateCommandPool");
    VkCommandBufferAllocateInfo commands = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
    commands.commandPool = g_commandPool;
    commands.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    commands.commandBufferCount = g_imageCount;
    CHECK(vkAllocateCommandBuffers(g_device, &commands, g_commands), "vkAllocateCommandBuffers");
    return true;
}

static bool create_vertices(void)
{
    VK_GET(g_instance, vkCreateBuffer)
    VK_GET(g_instance, vkDestroyBuffer)
    VK_GET(g_instance, vkAllocateMemory)
    VK_GET(g_instance, vkFreeMemory)
    VK_GET(g_instance, vkMapMemory)
    VK_GET(g_instance, vkBindBufferMemory)
    VK_GET(g_instance, vkGetBufferMemoryRequirements)
    VK_GET(g_instance, vkGetPhysicalDeviceMemoryProperties)

    VkBufferCreateInfo info = { VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
    info.size = 3 * 5 * sizeof(float);
    info.usage = VK_BUFFER_USAGE_VERTEX_BUFFER_BIT;
    info.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    CHECK(vkCreateBuffer(g_device, &info, NULL, &g_vertexBuffer), "vkCreateBuffer");
    VkMemoryRequirements needs;
    vkGetBufferMemoryRequirements(g_device, g_vertexBuffer, &needs);
    VkPhysicalDeviceMemoryProperties props;
    vkGetPhysicalDeviceMemoryProperties(g_physical, &props);
    uint32_t typeIndex = UINT32_MAX;
    for (uint32_t i = 0; i < props.memoryTypeCount; i++)
    {
        if ((needs.memoryTypeBits & (1u << i)) == 0) continue;
        if ((props.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) == 0) continue;
        if ((props.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_HOST_COHERENT_BIT) == 0) continue;
        typeIndex = i;
        break;
    }
    if (typeIndex == UINT32_MAX) { logf_("三角形：没有 host-visible coherent 内存类型"); return false; }
    VkMemoryAllocateInfo alloc = { VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
    alloc.allocationSize = needs.size;
    alloc.memoryTypeIndex = typeIndex;
    CHECK(vkAllocateMemory(g_device, &alloc, NULL, &g_vertexMemory), "vkAllocateMemory");
    CHECK(vkBindBufferMemory(g_device, g_vertexBuffer, g_vertexMemory, 0), "vkBindBufferMemory");
    CHECK(vkMapMemory(g_device, g_vertexMemory, 0, info.size, 0, (void**)&g_vertexMap), "vkMapMemory");
    // vertex 0 is the dragged one; the other two hold the triangle's base.
    g_vertexMap[5] = 0.6f; g_vertexMap[6] = 0.7f; g_vertexMap[7] = 0.0f; g_vertexMap[8] = 0.2f; g_vertexMap[9] = 1.0f;
    g_vertexMap[10] = -0.6f; g_vertexMap[11] = 0.7f; g_vertexMap[12] = 0.0f; g_vertexMap[13] = 0.2f; g_vertexMap[14] = 1.0f;
    return true;
}

static bool create_sync(void)
{
    VK_GET(g_instance, vkCreateSemaphore)
    VK_GET(g_instance, vkDestroySemaphore)
    VK_GET(g_instance, vkCreateFence)
    VK_GET(g_instance, vkDestroyFence)
    VK_GET(g_instance, vkResetFences)
    VK_GET(g_instance, vkWaitForFences)
    for (uint32_t i = 0; i < g_imageCount; i++)
    {
        VkSemaphoreCreateInfo sem = { VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
        CHECK(vkCreateSemaphore(g_device, &sem, NULL, &g_sync[i].acquire), "vkCreateSemaphore(acquire)");
        CHECK(vkCreateSemaphore(g_device, &sem, NULL, &g_sync[i].render), "vkCreateSemaphore(render)");
        VkFenceCreateInfo fence = { VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
        fence.flags = VK_FENCE_CREATE_SIGNALED_BIT;
        CHECK(vkCreateFence(g_device, &fence, NULL, &g_sync[i].fence), "vkCreateFence");
    }
    return true;
}

static void write_vertices(void)
{
    g_vertexMap[0] = g_dragX;
    g_vertexMap[1] = g_dragY;
    g_vertexMap[2] = 1.0f; g_vertexMap[3] = 0.3f; g_vertexMap[4] = 0.3f;
}

static void touch_input(void)
{
    Port17TouchEvent touches[32];
    int n = glfwAndroidPollTouches(touches, 32);
    static int dragging = 0;
    static int dragId = -1;
    for (int i = 0; i < n; i++)
    {
        Port17TouchEvent* t = &touches[i];
        if (t->type == 5)
        {
            logf_("三角形：软键盘动作键（type 5）");
            continue;
        }
        if (t->type == 1 && !dragging)
        {
            dragging = 1;
            dragId = t->id;
        }
        if (dragging && t->id == dragId && (t->type == 2 || t->type == 1))
        {
            // Finger pixels -> clip space, y down to y up.
            g_dragX = (float)t->x / (float)g_extent.width * 2.0f - 1.0f;
            g_dragY = 1.0f - (float)t->y / (float)g_extent.height * 2.0f;
        }
        if (t->type == 3 || t->type == 4)
        {
            if (t->id == dragId) dragging = 0;
        }
    }
}

int triangle_main(void)
{
    logf_("三角形：开始（GLFW 垫片 → libvulkan → MoltenVK → CAMetalLayer）");
    if (!glfwInit()) { logf_("三角形：glfwInit 失败"); return 1; }
    if (!glfwVulkanSupported()) { logf_("三角形：glfwVulkanSupported = FALSE（libvulkan.dylib 或 MoltenVK 没装上）"); return 2; }
    GLFWwindow* window = glfwCreateWindow(1920, 1080, "port17 triangle", NULL, NULL);
    if (!window)
    {
        const char* why = NULL;
        glfwGetError(&why);
        logf_("三角形：glfwCreateWindow 失败：%s", why ? why : "?");
        return 3;
    }
    glfwSetCharCallback(window, on_char);
    glfwSetKeyCallback(window, on_key);

    if (!create_instance()) return 4;
    VkResult surfaceResult = glfwCreateWindowSurface(g_instance, window, NULL, &g_surface);
    if (surfaceResult != 0) { logf_("三角形：glfwCreateWindowSurface 失败 VkResult=%d", (int)surfaceResult); return 5; }
    if (!pick_device_and_queue()) return 6;
    if (!create_swapchain()) return 7;
    if (!create_pipeline()) return 8;
    if (!create_vertices()) return 9;
    if (!create_sync()) return 10;
    VK_GET(g_instance, vkBeginCommandBuffer)
    VK_GET(g_instance, vkEndCommandBuffer)
    VK_GET(g_instance, vkResetCommandBuffer)
    VK_GET(g_instance, vkQueueSubmit)
    VK_GET(g_instance, vkQueuePresentKHR)
    VK_GET(g_instance, vkCmdBeginRenderPass)
    VK_GET(g_instance, vkCmdEndRenderPass)
    VK_GET(g_instance, vkCmdBindPipeline)
    VK_GET(g_instance, vkCmdSetViewport)
    VK_GET(g_instance, vkCmdSetScissor)
    VK_GET(g_instance, vkCmdBindVertexBuffers)
    VK_GET(g_instance, vkCmdDraw)
    VK_GET(g_instance, vkUnmapMemory)

    logf_("三角形：全部就绪，进入渲染循环（拖动手指移动顶点；字符回调计数）");
    long frames = 0, presents = 0, failures = 0;
    struct timespec start;
    clock_gettime(CLOCK_MONOTONIC, &start);
    int lastChars = 0, lastCharShown = -1;
    while (!g_stop && !glfwWindowShouldClose(window))
    {
        glfwPollEvents();
        touch_input();
        if (g_charCount != lastChars)
        {
            // A char came through the keyboard bridge (soft keyboard commit or hardware key text).
            lastChars = g_charCount;
            if (g_lastChar != lastCharShown)
            {
                logf_("三角形：char U+%04X（累计 %d）", g_lastChar, g_charCount);
                lastCharShown = g_lastChar;
            }
        }
        int w = 0, h = 0;
        glfwGetFramebufferSize(window, &w, &h);
        if (w == 0 || h == 0)
        {
            // The app is in the background: no drawable would come; idle until it returns.
            usleep(100 * 1000);
            continue;
        }
        FrameSync* sync = &g_sync[g_frameIndex];
        vkWaitForFences(g_device, 1, &sync->fence, VK_TRUE, UINT64_MAX);
        uint32_t imageIndex = 0;
        VkResult got = vkAcquireNextImageKHR(g_device, g_swapchain, UINT64_MAX, sync->acquire, VK_NULL_HANDLE, &imageIndex);
        if (got != VK_SUCCESS && got != VK_SUBOPTIMAL_KHR) { failures++; usleep(50 * 1000); continue; }
        vkResetFences(g_device, 1, &sync->fence);
        write_vertices();
        VkCommandBuffer cmd = g_commands[imageIndex];
        vkResetCommandBuffer(cmd, 0);
        VkCommandBufferBeginInfo begin = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
        vkBeginCommandBuffer(cmd, &begin);
        VkClearValue clear = { { { 0.05f, 0.05f, 0.08f, 1.0f } } };
        VkRenderPassBeginInfo passBegin = { VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO };
        passBegin.renderPass = g_renderPass;
        passBegin.framebuffer = g_framebuffers[imageIndex];
        passBegin.renderArea.extent.width = (uint32_t)w;
        passBegin.renderArea.extent.height = (uint32_t)h;
        passBegin.clearValueCount = 1;
        passBegin.pClearValues = &clear;
        vkCmdBeginRenderPass(cmd, &passBegin, VK_SUBPASS_CONTENTS_INLINE);
        vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, g_pipeline);
        VkViewport viewport = { 0, 0, (float)w, (float)h, 0, 1 };
        vkCmdSetViewport(cmd, 0, 1, &viewport);
        VkRect2D scissor = { { 0, 0 }, { (uint32_t)w, (uint32_t)h } };
        vkCmdSetScissor(cmd, 0, 1, &scissor);
        VkDeviceSize offset = 0;
        vkCmdBindVertexBuffers(cmd, 0, 1, &g_vertexBuffer, &offset);
        vkCmdDraw(cmd, 3, 1, 0, 0);
        vkCmdEndRenderPass(cmd);
        vkEndCommandBuffer(cmd);
        VkPipelineStageFlags waitStage = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
        VkSubmitInfo submit = { VK_STRUCTURE_TYPE_SUBMIT_INFO };
        submit.waitSemaphoreCount = 1;
        submit.pWaitSemaphores = &sync->acquire;
        submit.pWaitDstStageMask = &waitStage;
        submit.commandBufferCount = 1;
        submit.pCommandBuffers = &cmd;
        submit.signalSemaphoreCount = 1;
        submit.pSignalSemaphores = &sync->render;
        if (vkQueueSubmit(g_queue, 1, &submit, sync->fence) != VK_SUCCESS) { failures++; continue; }
        VkPresentInfoKHR present = { VK_STRUCTURE_TYPE_PRESENT_INFO_KHR };
        present.waitSemaphoreCount = 1;
        present.pWaitSemaphores = &sync->render;
        present.swapchainCount = 1;
        present.pSwapchains = &g_swapchain;
        present.pImageIndices = &imageIndex;
        VkResult shown = vkQueuePresentKHR(g_queue, &present);
        presents += shown == VK_SUCCESS || shown == VK_SUBOPTIMAL_KHR;
        if (shown != VK_SUCCESS && shown != VK_SUBOPTIMAL_KHR) failures++;
        g_frameIndex = (g_frameIndex + 1) % g_imageCount;
        frames++;
        if (frames == 1 || frames % 300 == 0)
        {
            struct timespec now;
            clock_gettime(CLOCK_MONOTONIC, &now);
            double seconds = (double)(now.tv_sec - start.tv_sec) + (double)(now.tv_nsec - start.tv_nsec) / 1e9;
            logf_("三角形：%ld 帧，%ld 次呈现成功，%ld 次失败，%.1f fps，顶点 (%.2f, %.2f)",
                  frames, presents, failures, seconds > 0 ? frames / seconds : 0.0, g_dragX, g_dragY);
        }
    }
    logf_("三角形：渲染循环结束（%ld 帧）", frames);
    vkDeviceWaitIdle(g_device);
    vkUnmapMemory(g_device, g_vertexMemory);
    return 0;
}
