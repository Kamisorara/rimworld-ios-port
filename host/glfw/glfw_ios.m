// The GLFW 3.4 shim for the iOS host (host/glfw, built as libglfw.dylib): the same interface the Android
// shim (rimworld-android-port host/glfw/glfw_android.c) gives the engine, on top of UIKit and a CAMetalLayer.
// The engine's Brutal.Glfw binds it by name ("glfw" -> libglfw.dylib).
//
// Threads: the main thread (UIKit) receives touches, key presses and application events through the
// glfwIos* entry points and turns them into a queue of small records. The engine thread calls
// glfwPollEvents / glfwWaitEventsTimeout, which drain that queue and invoke the GLFW callbacks there, so
// every callback runs on the thread that polls, as with real GLFW. The window is the host view's
// CAMetalLayer; the Vulkan surface comes from VK_EXT_metal_surface (vkCreateMetalSurfaceEXT).
//
// Touch is reported as a mouse: the first finger is the left button and moves the cursor; a second finger
// down is a right click at its position (the engine's PC input). Raw finger events are recorded for the
// managed touch layer in the same queue shape as on Android. The glfwAndroid* exports keep their names
// (the shared PortMobile P/Invokes them as "glfw" by these very names; renaming would fork the managed
// layer in M4); the iOS-specific attach entry points are glfwIos*.
//
// Coordinates: UIKit gives points; the engine gets drawable pixels (the window's own pixels, or the
// smaller render size when one is set: the layer's drawableSize is then that size and Metal scales the
// drawable to the layer's bounds — the compositor does the upscaling, no swapchain interposition needed).
#import <UIKit/UIKit.h>
#import <QuartzCore/QuartzCore.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#include <dlfcn.h>
#define API __attribute__((visibility("default"))) // 导出面宏（文件头：事件 sink 块在前面就用）
#include <errno.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#define VK_USE_PLATFORM_METAL_EXT
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include "glfw_ios.h"
#include "../app/unzip.h"

// unzip.c's progress callback is the host's old single-string entry; this shim has no overlay.
static void log_line(const char* fmt, ...);
void host_log(const char* msg)
{
    if (msg) log_line("%s", msg);
}

// The shim's own logging: routed into the host's log overlay/Documents file by glfwIosSetLogSink
// (installed before anything else); fprintf(stderr) as the fallback (the host redirects stderr to a file).
static void (*g_log_sink)(const char*) = NULL;
static void log_line(const char* fmt, ...)
{
    char text[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(text, sizeof text, fmt, ap);
    va_end(ap);
    if (g_log_sink) g_log_sink(text);
    fprintf(stderr, "[glfw] %s\n", text);
}

// ---- the managed bridge's event sink (窗口后端反转 v2) ----
// hooks/IosHost.cs 在 StartupHook 时注册：每个宿主事件（触点/按键/字符/前后台/尺寸）旁路
// 一份给它——引擎的 AndroidPlatform（WAYLITH_BACKEND=Android）靠这个队列喂数，与 GLFW 队列
// （PortMobile 的触控层读）并行不互斥。Port17IosEvent（glfw_ios.h）的字段顺序与 C# 的
// PortIosEvent 逐字段对齐（自然对齐）。
static void (*g_event_sink)(const Port17IosEvent*) = NULL;

API void glfwIosSetEventSink(void (*sink)(const Port17IosEvent*))
{
    g_event_sink = sink;
}

static int64_t boot_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static void emit_event(int kind, int64_t i0, int64_t i1, int64_t i2, float f0, float f1)
{
    if (!g_event_sink) return;
    Port17IosEvent e = { kind, i0, i1, i2, f0, f1, boot_ms() };
    g_event_sink(&e);
}

// ---- GLFW constants (glfw3.h, 3.4) ----
#define GLFW_TRUE 1
#define GLFW_FALSE 0
#define GLFW_RELEASE 0
#define GLFW_PRESS 1
#define GLFW_REPEAT 2
#define GLFW_KEY_UNKNOWN -1
#define GLFW_MOD_SHIFT 0x0001
#define GLFW_MOD_CONTROL 0x0002
#define GLFW_MOD_ALT 0x0004
#define GLFW_MOD_SUPER 0x0008
#define GLFW_MOD_CAPS_LOCK 0x0010
#define GLFW_MOD_NUM_LOCK 0x0020
#define GLFW_NO_ERROR 0
#define GLFW_NOT_INITIALIZED 0x00010001
#define GLFW_INVALID_ENUM 0x00010003
#define GLFW_INVALID_VALUE 0x00010004
#define GLFW_API_UNAVAILABLE 0x00010006
#define GLFW_PLATFORM_ERROR 0x00010008
#define GLFW_FEATURE_UNAVAILABLE 0x0001000C
#define GLFW_FOCUSED 0x00020001
#define GLFW_ICONIFIED 0x00020002
#define GLFW_RESIZABLE 0x00020003
#define GLFW_VISIBLE 0x00020004
#define GLFW_DECORATED 0x00020005
#define GLFW_AUTO_ICONIFY 0x00020006
#define GLFW_FLOATING 0x00020007
#define GLFW_MAXIMIZED 0x00020008
#define GLFW_CENTER_CURSOR 0x00020009
#define GLFW_TRANSPARENT_FRAMEBUFFER 0x0002000A
#define GLFW_HOVERED 0x0002000B
#define GLFW_FOCUS_ON_SHOW 0x0002000C
#define GLFW_MOUSE_PASSTHROUGH 0x0002000D
#define GLFW_CLIENT_API 0x00022001
#define GLFW_CONTEXT_CREATION_API 0x0002200B
#define GLFW_NO_API 0
#define GLFW_CURSOR 0x00033001
#define GLFW_STICKY_KEYS 0x00033002
#define GLFW_STICKY_MOUSE_BUTTONS 0x00033003
#define GLFW_LOCK_KEY_MODS 0x00033004
#define GLFW_RAW_MOUSE_MOTION 0x00033005
#define GLFW_CURSOR_NORMAL 0x00034001
#define GLFW_CONNECTED 0x00040001
#define GLFW_PLATFORM_NULL 0x00060005
#define GLFW_DONT_CARE -1

typedef struct GLFWmonitor GLFWmonitor;
typedef struct GLFWwindow GLFWwindow;
typedef struct GLFWcursor GLFWcursor;
typedef struct GLFWvidmode { int width, height, redBits, greenBits, blueBits, refreshRate; } GLFWvidmode;
typedef struct GLFWgammaramp { unsigned short* red; unsigned short* green; unsigned short* blue; unsigned int size; } GLFWgammaramp;
typedef struct GLFWimage { int width, height; unsigned char* pixels; } GLFWimage;
typedef struct GLFWgamepadstate { unsigned char buttons[15]; float axes[6]; } GLFWgamepadstate;
typedef struct GLFWallocator { void* allocate; void* reallocate; void* deallocate; void* user; } GLFWallocator;

typedef void (*GLFWerrorfun)(int, const char*);
typedef void (*GLFWwindowposfun)(GLFWwindow*, int, int);
typedef void (*GLFWwindowsizefun)(GLFWwindow*, int, int);
typedef void (*GLFWwindowclosefun)(GLFWwindow*);
typedef void (*GLFWwindowrefreshfun)(GLFWwindow*);
typedef void (*GLFWwindowfocusfun)(GLFWwindow*, int);
typedef void (*GLFWwindowiconifyfun)(GLFWwindow*, int);
typedef void (*GLFWwindowmaximizefun)(GLFWwindow*, int);
typedef void (*GLFWframebuffersizefun)(GLFWwindow*, int, int);
typedef void (*GLFWwindowcontentscalefun)(GLFWwindow*, float, float);
typedef void (*GLFWmousebuttonfun)(GLFWwindow*, int, int, int);
typedef void (*GLFWcursorposfun)(GLFWwindow*, double, double);
typedef void (*GLFWcursorenterfun)(GLFWwindow*, int);
typedef void (*GLFWscrollfun)(GLFWwindow*, double, double);
typedef void (*GLFWkeyfun)(GLFWwindow*, int, int, int, int);
typedef void (*GLFWcharfun)(GLFWwindow*, unsigned int);
typedef void (*GLFWcharmodsfun)(GLFWwindow*, unsigned int, int);
typedef void (*GLFWdropfun)(GLFWwindow*, int, const char**);
typedef void (*GLFWmonitorfun)(GLFWmonitor*, int);
typedef void (*GLFWjoystickfun)(int, int);
typedef void (*GLFWglproc)(void);
typedef void (*GLFWvkproc)(void);

// ---- the event queue, filled by the main thread, drained by the engine thread ----
enum { EV_WINDOW_INIT = 1, EV_WINDOW_TERM, EV_RESIZE, EV_FOCUS, EV_CURSOR, EV_BUTTON, EV_SCROLL, EV_KEY, EV_CHAR, EV_CLOSE };

typedef struct Event
{
    int type;
    int a, b, c, d;
    double x, y;
} Event;

#define QUEUE_CAP 4096
static Event g_queue[QUEUE_CAP];
static int g_qhead, g_qtail;
static pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t g_cond = PTHREAD_COND_INITIALIZER;

// ---- raw finger events for the managed touch layer (PortMobile), the Android queue shape ----
// (Port17TouchEvent is declared in glfw_ios.h; the managed side's struct layout is the same shape.)
#define TOUCH_CAP 2048
static Port17TouchEvent g_touches[TOUCH_CAP];
static int g_thead, g_ttail;
static int g_touchManaged;

static void record_touch(int type, int id, float x, float y)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    Port17TouchEvent e = { type, id, x, y, ts.tv_sec + ts.tv_nsec / 1e9 };
    pthread_mutex_lock(&g_lock);
    int next = (g_ttail + 1) % TOUCH_CAP;
    if (next == g_thead) g_thead = (g_thead + 1) % TOUCH_CAP;
    g_touches[g_ttail] = e;
    g_ttail = next;
    pthread_mutex_unlock(&g_lock);
}

// Shared state (under g_lock): what the app currently has.
static CAMetalLayer* g_layer;        // the host view's layer, never NULL after attach (set on the main thread)
static UIView* g_view;               // the host view (touch coordinates, safe area)
static UIView* g_keyboardField;      // the hidden text field of the keyboard bridge (main thread only)
static int g_width, g_height;        // the window's own size in pixels (drawable pixels = view points * scale)
static double g_scalePt = 2.0;       // points -> pixels (UIScreen.nativeScale, cached at attach)
static int g_renderWidth, g_renderHeight; // the size the game renders at (smaller than the window's), 0 for its own
static int g_focused = 1;
static float g_density = 3.0f;       // nativeScale; the Android shim's density is the same dp measure
static int g_refreshRate = 60;
static int g_windowVisible = 1;      // false while in the background (the layer stays, but no drawable comes)

// The coordinator stays alive while the system's sheet is up and the copy runs on a worker queue.
@class PickCoordinator;
static PickCoordinator* g_picker;

// The size the engine is told (under g_lock): the render size when one is set, else the window's.
static int engine_width_locked(void) { return g_renderWidth > 0 ? g_renderWidth : g_width; }
static int engine_height_locked(void) { return g_renderHeight > 0 ? g_renderHeight : g_height; }

static void update_scale_locked(void)
{
    // Finger events come in points; the engine gets pixels (its own window's, or the render size's).
    g_scalePt = g_width > 0 && g_view ? (double)g_width / g_view.bounds.size.width : 3.0;
}

// Engine-thread state (no lock needed; only the polling thread touches it).
static int g_initialized;
static int g_lastError;
static char g_lastErrorText[256];
static struct GLFWwindow { int dummy; } g_windowObject;
static struct GLFWmonitor { int dummy; } g_monitorObject;
static GLFWmonitor* g_monitors[1] = { &g_monitorObject };
static GLFWvidmode g_vidmode = { 1170, 2532, 8, 8, 8, 60 };
static struct GLFWcursor { int shape; } g_cursors[64];
static int g_cursorCount;
static int g_windowCreated;
static int g_shouldClose;
static int g_reportedFocused = 1;
static int g_reportedWidth, g_reportedHeight;
static int g_sizeAnnounced;
static double g_cursorX, g_cursorY;
static int g_mouseButtons[8];
static unsigned char g_keys[512];
static int g_cursorMode = GLFW_CURSOR_NORMAL;
static int g_stickyKeys, g_stickyButtons, g_lockKeyMods, g_rawMotion;
static void* g_windowUser;
static void* g_monitorUser;
static char g_title[256] = "Waylith";
static char* g_clipboard;
static struct timespec g_start;
static void* g_vulkan;
static int g_surfaceCreated;
static VkSurfaceKHR g_lastSurface;
static PFN_vkGetInstanceProcAddr g_vkGetInstanceProcAddr;

static GLFWerrorfun cb_error;
static GLFWmonitorfun cb_monitor;
static GLFWjoystickfun cb_joystick;
static GLFWwindowposfun cb_pos;
static GLFWwindowsizefun cb_size;
static GLFWwindowclosefun cb_close;
static GLFWwindowrefreshfun cb_refresh;
static GLFWwindowfocusfun cb_focus;
static GLFWwindowiconifyfun cb_iconify;
static GLFWwindowmaximizefun cb_maximize;
static GLFWframebuffersizefun cb_fbsize;
static GLFWwindowcontentscalefun cb_scale;
static GLFWmousebuttonfun cb_button;
static GLFWcursorposfun cb_cursor;
static GLFWcursorenterfun cb_enter;
static GLFWscrollfun cb_scroll;
static GLFWkeyfun cb_key;
static GLFWcharfun cb_char;
static GLFWcharmodsfun cb_charmods;
static GLFWdropfun cb_drop;

static void set_error(int code, const char* text)
{
    g_lastError = code;
    snprintf(g_lastErrorText, sizeof g_lastErrorText, "%s", text);
    log_line("glfw error 0x%x: %s", code, text);
    if (cb_error) cb_error(code, text);
}

// ---- main-thread side ----
static void push_locked(const Event* e)
{
    int next = (g_qtail + 1) % QUEUE_CAP;
    if (next == g_qhead)
    {
        // Full: drop the oldest. The engine was not polling for a long while (loading); old input is worthless.
        g_qhead = (g_qhead + 1) % QUEUE_CAP;
    }
    g_queue[g_qtail] = *e;
    g_qtail = next;
    pthread_cond_broadcast(&g_cond);
}

static void push(int type, int a, int b, int c, int d, double x, double y)
{
    Event e = { type, a, b, c, d, x, y };
    pthread_mutex_lock(&g_lock);
    push_locked(&e);
    pthread_mutex_unlock(&g_lock);
}

// The window's own size from the view (pixels): bounds * nativeScale. Called on the main thread.
static void window_pixels_from_view(void)
{
    if (!g_view) return;
    CGSize b = g_view.bounds.size;
    int w = (int)(b.width * g_density + 0.5);
    int h = (int)(b.height * g_density + 0.5);
    pthread_mutex_lock(&g_lock);
    if (w != g_width || h != g_height)
    {
        g_width = w;
        g_height = h;
        update_scale_locked();
        log_line("window %dx%d px (view %.0fx%.0f pt, scale %.1f)", w, h, b.width, b.height, g_density);
        if (g_windowVisible)
        {
            Event r = { EV_RESIZE, engine_width_locked(), engine_height_locked(), w, h, 0, 0 };
            push_locked(&r);
        }
    }
    pthread_mutex_unlock(&g_lock);
}

// Applies the drawable size for the current render size (or the window's own): the layer scales the
// drawable to its bounds, which is the compositor's upscaling. Called on the main thread only.
static void apply_drawable_size(void)
{
    if (!g_layer) return;
    pthread_mutex_lock(&g_lock);
    int w = engine_width_locked(), h = engine_height_locked();
    pthread_mutex_unlock(&g_lock);
    CGSize size = CGSizeMake(w > 0 ? w : 0, h > 0 ? h : 0);
    if (g_layer.drawableSize.width != size.width || g_layer.drawableSize.height != size.height)
    {
        g_layer.drawableSize = size;
        log_line("drawable size %.0fx%.0f px", size.width, size.height);
    }
}

// ---- the host's attach / event entry points (called from main.m, main thread) ----

API void glfwIosSetLogSink(void (*sink)(const char*))
{
    g_log_sink = sink;
}

API void glfwIosAttach(void* metalLayer, void* view, void* keyboardField)
{
    CAMetalLayer* layer = (CAMetalLayer*)metalLayer;
    UIView* hostView = (UIView*)view;
    g_layer = layer;
    g_view = hostView;
    g_keyboardField = (UIView*)keyboardField;
    UIScreen* screen = hostView.window.screen ?: UIScreen.mainScreen;
    g_density = screen.nativeScale;
    g_refreshRate = (int)screen.maximumFramesPerSecond;
    CGRect nb = screen.nativeBounds; // pixels, always portrait orientation of the device
    g_vidmode.width = (int)nb.size.width;
    g_vidmode.height = (int)nb.size.height;
    g_vidmode.refreshRate = g_refreshRate;
    if (layer.contentsScale != g_density) layer.contentsScale = g_density;
    window_pixels_from_view();
    apply_drawable_size();
    log_line("attached (screen %.0fx%.0f px, scale %.1f, %d Hz)", nb.size.width, nb.size.height, g_density, g_refreshRate);
}

API void glfwIosViewResized(void)
{
    window_pixels_from_view();
    apply_drawable_size();
    // 引擎的 surface/窗口尺寸跟着 drawable 走（渲染尺寸即换出 swapchain 尺寸）
    emit_event(5, 0, engine_width_locked(), engine_height_locked(), 0, 0);
}

// Application lifecycle (main thread): the background takes the window away (no drawable comes from a
// background CAMetalLayer), the foreground brings it back — the Android TERM/INIT window semantics.
API void glfwIosApplicationEvent(int event)
{
    // 归一给桥：1=active(resume) 2=resign(pause) 3=background(stop) 4=foreground(start)
    switch (event)
    {
    case GLFWIOS_DID_BECOME_ACTIVE: emit_event(4, 1, 0, 0, 0, 0); break;
    case GLFWIOS_WILL_RESIGN_ACTIVE: emit_event(4, 2, 0, 0, 0, 0); break;
    case GLFWIOS_DID_ENTER_BACKGROUND: emit_event(4, 3, 0, 0, 0, 0); break;
    case GLFWIOS_WILL_ENTER_FOREGROUND: emit_event(4, 4, 0, 0, 0, 0); break;
    default: break;
    }
    switch (event)
    {
    case GLFWIOS_WILL_RESIGN_ACTIVE:
    case GLFWIOS_DID_ENTER_BACKGROUND:
        pthread_mutex_lock(&g_lock);
        g_focused = 0;
        g_windowVisible = 0;
        log_line("window gone (app %s)", event == GLFWIOS_DID_ENTER_BACKGROUND ? "background" : "inactive");
        Event t = { EV_WINDOW_TERM, 0, 0, 0, 0, 0, 0 };
        push_locked(&t);
        Event f = { EV_FOCUS, 0, 0, 0, 0, 0, 0 };
        push_locked(&f);
        pthread_mutex_unlock(&g_lock);
        break;
    case GLFWIOS_WILL_ENTER_FOREGROUND:
    case GLFWIOS_DID_BECOME_ACTIVE:
        pthread_mutex_lock(&g_lock);
        g_focused = 1;
        g_windowVisible = 1;
        log_line("window back (app %s)", event == GLFWIOS_WILL_ENTER_FOREGROUND ? "foreground" : "active");
        Event i = { EV_WINDOW_INIT, engine_width_locked(), engine_height_locked(), g_width, g_height, 0, 0 };
        push_locked(&i);
        Event f2 = { EV_FOCUS, 1, 0, 0, 0, 0, 0 };
        push_locked(&f2);
        pthread_mutex_unlock(&g_lock);
        break;
    default:
        break;
    }
}

// A finger event from the host view (main thread): type 1 down, 2 move, 3 up, 4 cancel; x/y in points.
API void glfwIosTouch(int type, int id, float x, float y)
{
    pthread_mutex_lock(&g_lock);
    double sx = g_renderWidth > 0 && g_width > 0 ? (double)g_renderWidth / g_width : 1.0;
    double sy = g_renderHeight > 0 && g_height > 0 ? (double)g_renderHeight / g_height : 1.0;
    double px = x * g_scalePt * sx; // points -> window pixels -> engine pixels
    double py = y * g_scalePt * sy;
    pthread_mutex_unlock(&g_lock);
    record_touch(type, id, (float)px, (float)py);
    emit_event(1, type, id, 0, (float)px, (float)py); // 引擎的 AndroidPlatform 也要原始触点（窗口像素）
    if (g_touchManaged) return;

    // Mouse emulation (the PC input the engine is written for): first finger = cursor + left button,
    // second finger down = right button. The finger identities are kept between events, as on Android.
    static int primaryPointerId = -1, secondaryPointerId = -1;
    switch (type)
    {
    case 1: // down
        if (primaryPointerId < 0)
        {
            primaryPointerId = id;
            push(EV_CURSOR, 0, 0, 0, 0, px, py);
            push(EV_BUTTON, 0, GLFW_PRESS, 0, 0, px, py);
        }
        else if (secondaryPointerId < 0)
        {
            secondaryPointerId = id;
            push(EV_CURSOR, 0, 0, 0, 0, px, py);
            push(EV_BUTTON, 1, GLFW_PRESS, 0, 0, px, py);
        }
        break;
    case 2: // move
        if (id == primaryPointerId || id == secondaryPointerId)
            push(EV_CURSOR, 0, 0, 0, 0, px, py);
        break;
    case 3: // up
    case 4: // cancel
        if (id == secondaryPointerId)
        {
            push(EV_CURSOR, 0, 0, 0, 0, px, py);
            push(EV_BUTTON, 1, GLFW_RELEASE, 0, 0, px, py);
            secondaryPointerId = -1;
        }
        else if (id == primaryPointerId)
        {
            if (type == 3) push(EV_CURSOR, 0, 0, 0, 0, px, py);
            push(EV_BUTTON, 0, GLFW_RELEASE, 0, 0, px, py);
            primaryPointerId = -1;
        }
        break;
    default:
        break;
    }
}

// A hardware key press (main thread): glfwKey is a GLFW key code already (UIKey.keyCode is a USB HID
// usage; main.m maps it), mods the GLFW modifier mask.
API void glfwIosKey(int glfwKey, int action, int mods)
{
    if (glfwKey < 0) return;
    push(EV_KEY, glfwKey, 0, action, mods, 0, 0);
    emit_event(2, glfwKey, action, mods, 0, 0);
}

// Text from the hardware keyboard (UIKey.characters) or the managed layer.
API void glfwIosChar(unsigned int codepoint, int mods)
{
    if (codepoint == 0) return;
    push(EV_CHAR, (int)codepoint, mods, 0, 0, 0, 0);
    emit_event(3, codepoint, mods, 0, 0, 0);
}

// The soft keyboard's commit (main thread): every finger of the text as its own char event, the code
// points already whole (main.m turns the NSString into UTF-32).
API void glfwIosTextCommit(const unsigned int* codepoints, int count)
{
    for (int i = 0; i < count; i++)
        push(EV_CHAR, (int)codepoints[i], 0, 0, 0, 0, 0);
    log_line("keyboard committed %d chars", count);
}

// The soft keyboard's action key (Done / the keyboard's own enter). With the managed touch layer on it
// decides what it means (typing is finished); without it, it is the Enter key as on Android.
API void glfwIosEditorAction(void)
{
    if (g_touchManaged)
    {
        record_touch(5, -1, 0.0f, 0.0f);
        return;
    }
    push(EV_KEY, 257, 0, GLFW_PRESS, 0, 0, 0);
    push(EV_KEY, 257, 0, GLFW_RELEASE, 0, 0, 0);
}

// A backspace from the soft keyboard (the delegate sees it as an empty replacement): a press+release
// pair of the GLFW key, and no character.
API void glfwIosBackspace(void)
{
    push(EV_KEY, 259, 0, GLFW_PRESS, 0, 0, 0);
    push(EV_KEY, 259, 0, GLFW_RELEASE, 0, 0, 0);
}

// ---- the managed touch layer's API (PortMobile, P/Invoke "glfw"; the Android names) ----
API void glfwAndroidSetTouchMode(int managed)
{
    pthread_mutex_lock(&g_lock);
    g_touchManaged = managed;
    pthread_mutex_unlock(&g_lock);
    log_line("touch mode %s", managed ? "managed" : "mouse");
}

API int glfwAndroidPollTouches(Port17TouchEvent* out, int max)
{
    int n = 0;
    pthread_mutex_lock(&g_lock);
    while (n < max && g_thead != g_ttail)
    {
        out[n++] = g_touches[g_thead];
        g_thead = (g_thead + 1) % TOUCH_CAP;
    }
    pthread_mutex_unlock(&g_lock);
    return n;
}

// Injected events join the queue like the app's own and reach the engine at its next poll.
API void glfwAndroidInjectCursor(double x, double y) { push(EV_CURSOR, 0, 0, 0, 0, x, y); }
API void glfwAndroidInjectButton(int button, int action, int mods) { push(EV_BUTTON, button, action, mods, 0, 0, 0); }
API void glfwAndroidInjectScroll(double dx, double dy) { push(EV_SCROLL, 0, 0, 0, 0, dx, dy); }
API void glfwAndroidInjectKey(int key, int action, int mods) { push(EV_KEY, key, 0, action, mods, 0, 0); }
API void glfwAndroidInjectChar(unsigned int codepoint, int mods) { push(EV_CHAR, (int)codepoint, mods, 0, 0, 0, 0); }
API float glfwAndroidGetDensity(void) { return g_density; }

// The window's own size: the screen's resolution in pixels, whatever the game renders at.
API void glfwAndroidViewSize(int* width, int* height)
{
    pthread_mutex_lock(&g_lock);
    if (width) *width = g_width;
    if (height) *height = g_height;
    pthread_mutex_unlock(&g_lock);
}

// The display cutouts (the notch, the home indicator), as the Android shim's cutoutRects(): the safe
// area's edges as rects "left,top,right,bottom;..." in the engine's pixels. Copied into out; returns length.
API int glfwAndroidCutout(char* out, int size)
{
    if (!out || size <= 0) return 0;
    out[0] = 0;
    if (!g_view) return 0;
    UIEdgeInsets safe = g_view.safeAreaInsets; // points
    pthread_mutex_lock(&g_lock);
    int ew = engine_width_locked(), eh = engine_height_locked(); // the engine's pixels
    double sx = g_width > 0 ? (double)ew / g_width : 1.0;
    double sy = g_height > 0 ? (double)eh / g_height : 1.0;
    pthread_mutex_unlock(&g_lock);
    double px = g_scalePt * sx, py = g_scalePt * sy; // points -> engine pixels
    double top = safe.top * py, bottom = safe.bottom * py, left = safe.left * px, right = safe.right * px;
    int length = 0;
    #define CUTOUT_RECT(l, t, r, b) \
        if ((b) > (t) && (r) > (l) && length < size - 64) \
            length += snprintf(out + length, (size_t)(size - length), "%s%d,%d,%d,%d", \
                               length > 0 ? ";" : "", (int)(l), (int)(t), (int)((r) + 0.999), (int)((b) + 0.999));
    CUTOUT_RECT(0, 0, ew, top)                        // the notch's side
    CUTOUT_RECT(0, eh - bottom, ew, eh)               // the home indicator
    CUTOUT_RECT(0, top, left, eh - bottom)            // the left edge (landscape)
    CUTOUT_RECT(ew - right, top, ew, eh - bottom)     // the right edge (landscape)
    #undef CUTOUT_RECT
    return length;
}

// The engine sets a resolution (the options' Resolution, Screen.SetResolution): the size the game
// renders at, the layer's drawableSize — Metal scales the drawable to the view's bounds. Only sizes of
// the window's shape, smaller than it, are taken (as on Android); the window's own size puts it back.
static void request_render_size(int width, int height)
{
    pthread_mutex_lock(&g_lock);
    int ww = g_width, wh = g_height;
    pthread_mutex_unlock(&g_lock);
    if (width <= 0 || height <= 0 || ww <= 0 || wh <= 0) return;
    int rw = 0, rh = 0;
    if (width != ww || height != wh)
    {
        double shape = (double)width / height, window = (double)ww / wh;
        if (width > ww || height > wh || shape < window * 0.98 || shape > window * 1.02)
        {
            log_line("resolution %dx%d not taken (the window is %dx%d)", width, height, ww, wh);
            return;
        }
        rw = width;
        rh = height;
    }
    pthread_mutex_lock(&g_lock);
    if (rw == g_renderWidth && rh == g_renderHeight)
    {
        pthread_mutex_unlock(&g_lock);
        return;
    }
    g_renderWidth = rw;
    g_renderHeight = rh;
    pthread_mutex_unlock(&g_lock);
    // The layer is the main thread's: the drawable size is applied there, then the engine hears the new size.
    dispatch_async(dispatch_get_main_queue(), ^{
        apply_drawable_size();
        pthread_mutex_lock(&g_lock);
        Event r = { EV_RESIZE, engine_width_locked(), engine_height_locked(), g_width, g_height, 0, 0 };
        push_locked(&r);
        pthread_mutex_unlock(&g_lock);
        log_line("rendering at %dx%d for a window of %dx%d", rw > 0 ? rw : g_width, rh > 0 ? rh : g_height, g_width, g_height);
    });
}

// The soft keyboard: the hidden text field becomes / resigns first responder (UIKit's rule: main thread).
API void glfwAndroidShowKeyboard(int show)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        if (!g_keyboardField) return;
        BOOL shown = g_keyboardField.isFirstResponder;
        if (show && !shown) [g_keyboardField becomeFirstResponder];
        if (!show && shown) [g_keyboardField resignFirstResponder];
        log_line("soft keyboard %s", show ? "shown" : "hidden");
    });
}

// iOS has no input-method policy of Android's kind: the soft keyboard is always the one for text fields.
API void glfwAndroidKeyboardForced(int forced)
{
    log_line("soft keyboard %s (iOS: always up for text fields)", forced ? "forced up" : "left to the system");
}

// The devices the last mouse event and the last letter or digit key came from (mobile/InputMode.cs counts
// a mouse or keyboard the system does not call external once used). iOS reports none for now: external
// keyboards come in as UIKey presses without a device identity, mice as touches (no pointer type yet).
API void glfwAndroidLastDevices(int* mouse, int* key)
{
    if (mouse) *mouse = -1;
    if (key) *key = -1;
}

// The external mice and keyboards (mobile/InputMode.cs): "<bits>|<names>" on Android. Empty on iOS.
API int glfwAndroidInputDevices(char* out, int size)
{
    if (out && size > 0) out[0] = 0;
    return 0;
}

// ---- the Files app chooser (ModImport/SaveImport) ----
// Android hands the chooser to MainActivity and it copies into files/ModImport. iOS's counterpart is a
// UIDocumentPicker sheet: .zip/.rws files are opened in place (several at once; the security scope is held
// while the file is read, so no copy is made before the zip is unpacked), one folder is opened with security
// scope and copied. zips are unpacked here so the shared C# importer keeps seeing folders.
enum { PICK_CHOOSING = 1, PICK_COPYING, PICK_DONE, PICK_FAILED, PICK_CANCELLED };

static void pick_paths(char* staging, size_t stagingSize, char* state, size_t stateSize)
{
    snprintf(staging, stagingSize, "%s/Documents/ModImport", NSHomeDirectory().UTF8String);
    snprintf(state, stateSize, "%s/Documents/ModImport.state", NSHomeDirectory().UTF8String);
}

static void pick_write_state(int state, int files, long long bytes, NSString* message)
{
    char staging[1024], statePath[1024], tempPath[1080];
    pick_paths(staging, sizeof staging, statePath, sizeof statePath);
    snprintf(tempPath, sizeof tempPath, "%s.tmp", statePath);
    FILE* out = fopen(tempPath, "wb");
    if (!out) return;
    NSData* text = [[NSString stringWithFormat:@"%d %d %lld\n%@", state, files, bytes,
                     message ? message : @""] dataUsingEncoding:NSUTF8StringEncoding];
    BOOL ok = fwrite(text.bytes, 1, text.length, out) == text.length;
    ok = fclose(out) == 0 && ok;
    if (!ok || [[NSFileManager defaultManager] moveItemAtPath:[NSString stringWithUTF8String:tempPath]
                                                        toPath:[NSString stringWithUTF8String:statePath]
                                                        error:NULL] == NO)
        unlink(tempPath);
}

static NSString* pick_safe_name(NSString* name)
{
    NSString* clean = [[name componentsSeparatedByCharactersInSet:
        [NSCharacterSet characterSetWithCharactersInString:@"/\\:*?\"<>|"]]
        componentsJoinedByString:@"_"];
    clean = [clean stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];
    return clean.length == 0 || [clean isEqualToString:@"."] || [clean isEqualToString:@".."] ? @"item" : clean;
}

// Two chosen items can share a name (a.zip from two folders): the second would unpack into the first one's
// folder. The staging is emptied before the picker, so an existing name is from this batch; the next free
// number keeps the two apart.
static NSString* pick_target_name(NSString* root, NSString* name, NSMutableSet* used)
{
    NSString* candidate = name;
    for (int n = 2; [used containsObject:candidate] ||
                    [[NSFileManager defaultManager] fileExistsAtPath:[root stringByAppendingPathComponent:candidate]]; n++)
        candidate = [NSString stringWithFormat:@"%@ (%d)", name, n];
    [used addObject:candidate];
    return candidate;
}

static void pick_reset_staging(void)
{
    char staging[1024], state[1024];
    pick_paths(staging, sizeof staging, state, sizeof state);
    NSFileManager* files = NSFileManager.defaultManager;
    [files removeItemAtPath:[NSString stringWithUTF8String:staging] error:NULL];
    [files removeItemAtPath:[NSString stringWithUTF8String:state] error:NULL];
    [files createDirectoryAtPath:[NSString stringWithUTF8String:staging]
         withIntermediateDirectories:YES attributes:nil error:NULL];
}

static long long pick_tree_bytes(NSString* root, int* files)
{
    NSDirectoryEnumerator<NSURL*>* items = [NSFileManager.defaultManager
        enumeratorAtURL:[NSURL fileURLWithPath:root]
        includingPropertiesForKeys:@[NSURLFileSizeKey] options:0 errorHandler:^BOOL(NSURL*, NSError*) { return YES; }];
    long long bytes = 0;
    for (NSURL* item in items)
    {
        NSNumber* size = nil;
        if ([item getResourceValue:&size forKey:NSURLFileSizeKey error:NULL] && size)
        {
            bytes += size.longLongValue;
            if (files) (*files)++;
        }
    }
    return bytes;
}

static UIViewController* pick_root_controller(void)
{
    UIViewController* root = nil;
    for (UIScene* scene in UIApplication.sharedApplication.connectedScenes)
    {
        if (![scene isKindOfClass:[UIWindowScene class]] ||
            scene.activationState != UISceneActivationStateForegroundActive) continue;
        for (UIWindow* window in ((UIWindowScene*)scene).windows)
            if (window.isKeyWindow) root = window.rootViewController;
    }
    if (!root) root = UIApplication.sharedApplication.keyWindow.rootViewController;
    while (root.presentedViewController) root = root.presentedViewController;
    return root;
}

@interface PickCoordinator : NSObject <UIDocumentPickerDelegate>
@property (nonatomic, readonly) BOOL wantsFolder;
@property (nonatomic) int files;
@property (nonatomic) long long bytes;
@property (nonatomic) BOOL active;
@end

@implementation PickCoordinator
- (instancetype)initWithFolder:(BOOL)folder
{
    if ((self = [super init])) { _wantsFolder = folder; _active = YES; }
    return self;
}

- (void)documentPicker:(UIDocumentPickerViewController*)controller didPickDocumentsAtURLs:(NSArray<NSURL*>*)urls
{
    (void)controller;
    [self setPhase:PICK_COPYING message:@""];
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
        @autoreleasepool { [self importURLs:urls]; }
    });
}

- (void)documentPickerWasCancelled:(UIDocumentPickerViewController*)controller
{
    (void)controller;
    self.active = NO;
    pick_write_state(PICK_CANCELLED, 0, 0, @"");
    [g_picker release];
    g_picker = nil;
}

- (void)setPhase:(int)state message:(NSString*)message
{
    pick_write_state(state, self.files, self.bytes, message);
}

- (void)importURLs:(NSArray<NSURL*>*)urls
{
    char staging[1024], state[1024];
    pick_paths(staging, sizeof staging, state, sizeof state);
    NSString* root = [NSString stringWithUTF8String:staging];
    NSMutableString* leftOut = [NSMutableString string];
    NSString* failure = nil;
    NSMutableSet* used = [NSMutableSet set];

    for (NSURL* url in urls)
    {
        BOOL accessed = [url startAccessingSecurityScopedResource];
        @try
        {
            NSString* name = pick_safe_name(url.lastPathComponent);
            NSString* lower = name.lowercaseString;
            NSString* target = nil;
            if (self.wantsFolder)
            {
                target = [root stringByAppendingPathComponent:pick_target_name(root, name, used)];
                [[NSFileManager defaultManager] removeItemAtPath:target error:NULL];
                NSError* error = nil;
                if ([[NSFileManager defaultManager] copyItemAtPath:url.path toPath:target error:&error])
                {
                    self.files = 0;
                    int added = 0;
                    self.bytes = pick_tree_bytes(target, &added);
                    self.files = added;
                }
                else failure = [NSString stringWithFormat:@"%@: %@", name, error.localizedDescription ?: @"copy failed"];
            }
            else if ([lower hasSuffix:@".zip"])
            {
                NSString* stem = [name substringToIndex:name.length - 4];
                if (stem.length == 0) stem = @"zip";
                target = [root stringByAppendingPathComponent:pick_target_name(root, stem, used)];
                if (port17_unzip(url.path.UTF8String, target.UTF8String) == 0)
                {
                    int added = 0;
                    self.bytes += pick_tree_bytes(target, &added);
                    self.files += added;
                }
                else failure = [NSString stringWithFormat:@"%@: the zip could not be unpacked", name];
            }
            else if ([lower hasSuffix:@".rws"])
            {
                target = [root stringByAppendingPathComponent:pick_target_name(root, name, used)];
                if ([[NSFileManager defaultManager] copyItemAtPath:url.path toPath:target error:nil])
                {
                    self.files += 1;
                    NSDictionary* attrs = [[NSFileManager defaultManager] attributesOfItemAtPath:target error:nil];
                    self.bytes += attrs[NSFileSize] ? [attrs[NSFileSize] longLongValue] : 0;
                }
                else failure = [NSString stringWithFormat:@"%@: copy failed", name];
            }
            else
            {
                [leftOut appendFormat:@"%@\n", name];
            }
        }
        @catch (NSException* exception)
        {
            failure = [NSString stringWithFormat:@"%@: %@", url.lastPathComponent, exception.reason];
        }
        @finally
        {
            if (accessed) [url stopAccessingSecurityScopedResource];
        }
        if (failure) break;
    }

    self.active = NO;
    if (failure)
        [self setPhase:PICK_FAILED message:failure];
    else
        [self setPhase:PICK_DONE message:leftOut];
    [self release];
    g_picker = nil;
}
@end

// 1 means the sheet was handed to UIKit; the copy progress is ModImport.state, exactly as on Android.
API int glfwAndroidPickFiles(int folder)
{
    @autoreleasepool {
        if (!g_view) return 0;
        if (g_picker)
        {
            if (g_picker.active) return 0;
            [g_picker release];
            g_picker = nil;
        }
        pick_reset_staging();
        pick_write_state(PICK_CHOOSING, 0, 0, @"");
        PickCoordinator* picker = [[PickCoordinator alloc] initWithFolder:folder != 0];
        g_picker = picker;
        dispatch_async(dispatch_get_main_queue(), ^{
            @autoreleasepool {
        UIDocumentPickerViewController* sheet = nil;
        if (picker.wantsFolder)
            sheet = [[UIDocumentPickerViewController alloc] initForOpeningContentTypes:
                     @[[UTType typeWithIdentifier:@"public.folder"]]];
        else
            // Opening, not importing as a copy: the zip is read in place while the security scope is held, so a
            // large mod archive does not take a second copy of itself in the app's temporary storage first.
            sheet = [[UIDocumentPickerViewController alloc] initForOpeningContentTypes:
                     @[[UTType typeWithIdentifier:@"public.zip-archive"], [UTType typeWithIdentifier:@"public.data"]]];
        sheet.delegate = picker;
        sheet.allowsMultipleSelection = !picker.wantsFolder;
        UIViewController* root = pick_root_controller();
        if (!root)
        {
                [sheet release];
                [g_picker release];
            g_picker = nil;
            pick_write_state(PICK_FAILED, 0, 0, @"no presented view controller");
            return;
        }
        [root presentViewController:sheet animated:YES completion:nil];
        // UIKit keeps the presented controller; under MRC the alloc is released here.
        [sheet release];
        log_line("mod picker: %s", picker.wantsFolder ? "folder" : "zip/save files");
        }
            });
        }
    return 1;
}

// A link opened in the phone's app for it (mobile/OpenUrl.cs): 1 when an app was asked.
API int glfwAndroidOpenUrl(const char* url)
{
    if (!url || !*url) return 0;
    NSString* s = [NSString stringWithUTF8String:url];
    if (![s hasPrefix:@"http://"] && ![s hasPrefix:@"https://"]) return 0;
    __block int ok = 0;
    dispatch_sync(dispatch_get_main_queue(), ^{
        NSURL* ns = [NSURL URLWithString:s];
        UIApplication* app = UIApplication.sharedApplication;
        // openURL:options:completionHandler: 回 void 且异步——"有没有 app 接"由 canOpenURL 答，
        // 打开本身在 completion 落定（Android 语义就是"问了没有"，不是"打开了没有"）。
        if ([app canOpenURL:ns])
        {
            [app openURL:ns options:@{} completionHandler:nil];
            ok = 1;
        }
    });
    return ok;
}

// The system's Files app for a folder (mobile/OpenUrl.cs): on iOS the game's documents are visible in
// the Files app already; opening a specific folder needs a UIDocumentBrowserViewController — later (M5).
API int glfwAndroidOpenFolder(const char* path) { (void)path; return 0; }

// A picture the gallery should list (mobile/Screenshots.cs): iOS's photo library needs a save through
// PHPhotoLibrary, not a scan of a file — later.
API int glfwAndroidScanMedia(const char* path) { (void)path; return 0; }

// The proxy the system gives a web address (hooks/SystemProxy.cs): read from CFNetwork's settings — later.
API int glfwAndroidProxyFor(const char* url, char* out, int size)
{
    (void)url;
    if (out && size > 0) out[0] = 0;
    return 0;
}

// Android's performance hints: no APerformanceHintManager on iOS. Game Mode (iOS 18) has no process API
// a plain app may call; the frame pacing is the display link's. Reported as not available.
API int glfwAndroidPerfHint(int mainTid, int renderTid, long long targetNanos)
{
    (void)mainTid; (void)renderTid; (void)targetNanos;
    return 0;
}
API int glfwAndroidPerfHintConfig(int mainTid, int renderTid, long long targetNanos, int flags)
{
    (void)mainTid; (void)renderTid; (void)targetNanos; (void)flags;
    return 0;
}
API void glfwAndroidPerfReport(long long actualNanos) { (void)actualNanos; }

// The frame rate the game means to draw at, told to the compositor. A CAMetalLayer has no vote of its
// own; the frame rate comes out of how fast frames are presented (and the ProMotion policy of the app).
API int glfwAndroidSetFrameRate(float framesPerSecond) { (void)framesPerSecond; return 0; }

// The display's refresh rate at the moment (UIScreen.maximumFramesPerSecond), 0 when it cannot be asked.
API float glfwAndroidDisplayRate(void)
{
    return (float)g_refreshRate;
}

// The game is about to quit ("quit to desktop": Application.Quit, then the runtime's own exit). On iOS
// there is no activity to finish; the process simply ends, and it should end quickly: a handler
// registered now, which exit() therefore runs first, ends the process before the runtime's teardown
// pulls things from under threads UIKit still runs (the SIGABRT Android's exit path showed).
static void exit_now(void)
{
    _exit(0);
}

API void glfwAndroidPrepareExit(void)
{
    static int prepared;
    if (prepared) return;
    prepared = 1;
    atexit(exit_now);
    log_line("the game is quitting");
}

// 安卓版是 call_activity("restartGame")——activity 重建；iOS 的 app 不能自己拉起自己，
// 唯一合法形态是干净退出（用户回主屏再点一次）。exit(0) 走正常终止（_exit 不行：
// 跳过 atexit 与文件缓冲冲洗），PortMobile 的重开对话按"已重启"继续。
API int glfwAndroidRestart(void)
{
    log_line("glfw: restart of the game asked for - clean exit (iOS cannot relaunch itself; reopen the app)");
    exit(0);
    return 0;
}

// ---- engine-thread side: dispatching ----
static void dispatch(const Event* e)
{
    GLFWwindow* w = &g_windowObject;
    switch (e->type)
    {
    case EV_WINDOW_INIT:
    case EV_RESIZE:
        if (e->a != g_reportedWidth || e->b != g_reportedHeight || !g_sizeAnnounced)
        {
            g_reportedWidth = e->a;
            g_reportedHeight = e->b;
            if (e->type == EV_WINDOW_INIT && e->c > 0)
            {
                g_vidmode.width = e->c;
                g_vidmode.height = e->d;
            }
            if (g_windowCreated)
            {
                // Like GLFW when a window is shown: the size and the framebuffer size are announced. The
                // managed window only learns its framebuffer size from this callback, and treats 0x0 as
                // minimized.
                g_sizeAnnounced = 1;
                if (cb_size) cb_size(w, e->a, e->b);
                if (cb_fbsize) cb_fbsize(w, e->a, e->b);
            }
        }
        if (g_windowCreated && cb_refresh && e->type == EV_WINDOW_INIT) cb_refresh(w);
        break;
    case EV_WINDOW_TERM:
        // No window: the framebuffer is 0x0, which the engine treats as minimized and stops rendering;
        // the size is announced again with the next window.
        g_sizeAnnounced = 0;
        if (g_windowCreated)
        {
            if (cb_size) cb_size(w, 0, 0);
            if (cb_fbsize) cb_fbsize(w, 0, 0);
        }
        break;
    case EV_FOCUS:
        if (g_reportedFocused != e->a)
        {
            g_reportedFocused = e->a;
            if (!e->a)
            {
                // Like GLFW: releasing everything when focus goes away.
                for (int k = 0; k < 512; k++)
                {
                    if (g_keys[k]) { g_keys[k] = 0; if (cb_key) cb_key(w, k, 0, GLFW_RELEASE, 0); }
                }
                for (int b = 0; b < 8; b++)
                {
                    if (g_mouseButtons[b]) { g_mouseButtons[b] = 0; if (cb_button) cb_button(w, b, GLFW_RELEASE, 0); }
                }
            }
            if (g_windowCreated && cb_focus) cb_focus(w, e->a);
        }
        break;
    case EV_CURSOR:
        g_cursorX = e->x;
        g_cursorY = e->y;
        if (cb_cursor) cb_cursor(w, e->x, e->y);
        break;
    case EV_BUTTON:
        if (e->a >= 0 && e->a < 8) g_mouseButtons[e->a] = e->b == GLFW_PRESS;
        if (cb_button) cb_button(w, e->a, e->b, e->c);
        break;
    case EV_SCROLL:
        if (cb_scroll) cb_scroll(w, e->x, e->y);
        break;
    case EV_KEY:
        if (e->a >= 0 && e->a < 512) g_keys[e->a] = e->c != GLFW_RELEASE;
        if (cb_key) cb_key(w, e->a, e->b, e->c, e->d);
        break;
    case EV_CHAR:
        if (cb_char) cb_char(w, (unsigned int)e->a);
        if (cb_charmods) cb_charmods(w, (unsigned int)e->a, e->b);
        break;
    case EV_CLOSE:
        g_shouldClose = 1;
        if (cb_close) cb_close(w);
        break;
    }
}

static void drain(void)
{
    for (;;)
    {
        Event e;
        pthread_mutex_lock(&g_lock);
        if (g_qhead == g_qtail)
        {
            pthread_mutex_unlock(&g_lock);
            return;
        }
        e = g_queue[g_qhead];
        g_qhead = (g_qhead + 1) % QUEUE_CAP;
        pthread_mutex_unlock(&g_lock);
        dispatch(&e);
    }
}

static void timespec_after(struct timespec* ts, double seconds)
{
    clock_gettime(CLOCK_REALTIME, ts);
    double whole = (double)(long)seconds;
    ts->tv_sec += (long)whole;
    ts->tv_nsec += (long)((seconds - whole) * 1e9);
    if (ts->tv_nsec >= 1000000000L) { ts->tv_sec++; ts->tv_nsec -= 1000000000L; }
}

// Waits (with g_lock held by the caller) until the app has a visible window, up to the timeout. On iOS
// the layer exists from launch; "no window" is only the background state.
static int wait_for_window_locked(double seconds)
{
    struct timespec until;
    timespec_after(&until, seconds);
    while (!g_windowVisible)
    {
        if (pthread_cond_timedwait(&g_cond, &g_lock, &until) == ETIMEDOUT) break;
    }
    return g_windowVisible;
}

// ---- GLFW API ----
API int glfwInit(void)
{
    if (!g_initialized)
    {
        clock_gettime(CLOCK_MONOTONIC, &g_start);
        g_initialized = 1;
        pthread_mutex_lock(&g_lock);
        g_reportedWidth = engine_width_locked();
        g_reportedHeight = engine_height_locked();
        g_reportedFocused = g_focused;
        if (g_width > 0) { g_vidmode.width = g_width; g_vidmode.height = g_height; }
        pthread_mutex_unlock(&g_lock);
        log_line("glfwInit (iOS shim)");
    }
    return GLFW_TRUE;
}

API void glfwTerminate(void) { g_initialized = 0; g_windowCreated = 0; }
API void glfwInitHint(int hint, int value) { (void)hint; (void)value; }
API void glfwInitAllocator(const GLFWallocator* allocator) { (void)allocator; }
API void glfwInitVulkanLoader(void* loader) { (void)loader; }
API void glfwGetVersion(int* major, int* minor, int* rev) { if (major) *major = 3; if (minor) *minor = 4; if (rev) *rev = 0; }
API const char* glfwGetVersionString(void) { return "3.4.0 iOS (port17 shim)"; }

API int glfwGetError(const char** description)
{
    int code = g_lastError;
    if (description) *description = code ? g_lastErrorText : NULL;
    g_lastError = 0;
    return code;
}

API GLFWerrorfun glfwSetErrorCallback(GLFWerrorfun f) { GLFWerrorfun old = cb_error; cb_error = f; return old; }
API int glfwGetPlatform(void) { return GLFW_PLATFORM_NULL; }
API int glfwPlatformSupported(int platform) { return platform == GLFW_PLATFORM_NULL; }

// Monitors
API GLFWmonitor** glfwGetMonitors(int* count) { if (count) *count = 1; return g_monitors; }
API GLFWmonitor* glfwGetPrimaryMonitor(void) { return &g_monitorObject; }
API void glfwGetMonitorPos(GLFWmonitor* m, int* x, int* y) { (void)m; if (x) *x = 0; if (y) *y = 0; }
API void glfwGetMonitorWorkarea(GLFWmonitor* m, int* x, int* y, int* w, int* h) { (void)m; if (x) *x = 0; if (y) *y = 0; if (w) *w = g_vidmode.width; if (h) *h = g_vidmode.height; }
API void glfwGetMonitorPhysicalSize(GLFWmonitor* m, int* w, int* h)
{
    (void)m;
    // UIScreen has no physical size API; the Android shim's estimate (dpi = density * 160) carries over.
    float dpi = g_density * 160.0f;
    if (w) *w = (int)(g_vidmode.width / dpi * 25.4f);
    if (h) *h = (int)(g_vidmode.height / dpi * 25.4f);
}
API void glfwGetMonitorContentScale(GLFWmonitor* m, float* x, float* y) { (void)m; if (x) *x = 1.0f; if (y) *y = 1.0f; }
API const char* glfwGetMonitorName(GLFWmonitor* m) { (void)m; return "iOS display"; }
API void glfwSetMonitorUserPointer(GLFWmonitor* m, void* p) { (void)m; g_monitorUser = p; }
API void* glfwGetMonitorUserPointer(GLFWmonitor* m) { (void)m; return g_monitorUser; }
API GLFWmonitorfun glfwSetMonitorCallback(GLFWmonitorfun f) { GLFWmonitorfun old = cb_monitor; cb_monitor = f; return old; }
API const GLFWvidmode* glfwGetVideoModes(GLFWmonitor* m, int* count) { (void)m; if (count) *count = 1; return &g_vidmode; }
API const GLFWvidmode* glfwGetVideoMode(GLFWmonitor* m) { (void)m; return &g_vidmode; }
API void glfwSetGamma(GLFWmonitor* m, float gamma) { (void)m; (void)gamma; }
API const GLFWgammaramp* glfwGetGammaRamp(GLFWmonitor* m) { (void)m; set_error(GLFW_FEATURE_UNAVAILABLE, "gamma ramps are not available"); return NULL; }
API void glfwSetGammaRamp(GLFWmonitor* m, const GLFWgammaramp* ramp) { (void)m; (void)ramp; }

// Window
API void glfwDefaultWindowHints(void) {}
API void glfwWindowHint(int hint, int value) { (void)hint; (void)value; }
API void glfwWindowHintString(int hint, const char* value) { (void)hint; (void)value; }

API GLFWwindow* glfwCreateWindow(int width, int height, const char* title, GLFWmonitor* monitor, GLFWwindow* share)
{
    (void)monitor; (void)share;
    if (!g_initialized) { set_error(GLFW_NOT_INITIALIZED, "GLFW is not initialized"); return NULL; }
    if (g_windowCreated) { set_error(GLFW_PLATFORM_ERROR, "only one window is available on iOS"); return NULL; }
    pthread_mutex_lock(&g_lock);
    int have = wait_for_window_locked(20.0);
    if (have)
    {
        g_reportedWidth = engine_width_locked();
        g_reportedHeight = engine_height_locked();
        g_vidmode.width = g_width;
        g_vidmode.height = g_height;
        g_reportedFocused = g_focused;
    }
    pthread_mutex_unlock(&g_lock);
    if (!have)
    {
        set_error(GLFW_PLATFORM_ERROR, "the app has no window");
        return NULL;
    }
    if (title) snprintf(g_title, sizeof g_title, "%s", title);
    g_windowCreated = 1;
    g_shouldClose = 0;
    g_sizeAnnounced = 0;
    push(EV_RESIZE, g_reportedWidth, g_reportedHeight, 0, 0, 0, 0);
    log_line("glfwCreateWindow: using the app window %dx%d (asked for %dx%d)", g_reportedWidth, g_reportedHeight, width, height);
    return &g_windowObject;
}

API void glfwDestroyWindow(GLFWwindow* w) { (void)w; g_windowCreated = 0; }
API int glfwWindowShouldClose(GLFWwindow* w) { (void)w; return g_shouldClose; }
API void glfwSetWindowShouldClose(GLFWwindow* w, int value) { (void)w; g_shouldClose = value; }
API const char* glfwGetWindowTitle(GLFWwindow* w) { (void)w; return g_title; }
API void glfwSetWindowTitle(GLFWwindow* w, const char* title) { (void)w; if (title) snprintf(g_title, sizeof g_title, "%s", title); }
API void glfwSetWindowIcon(GLFWwindow* w, int count, const GLFWimage* images) { (void)w; (void)count; (void)images; }
API void glfwGetWindowPos(GLFWwindow* w, int* x, int* y) { (void)w; if (x) *x = 0; if (y) *y = 0; }
API void glfwSetWindowPos(GLFWwindow* w, int x, int y) { (void)w; (void)x; (void)y; }
API void glfwGetWindowSize(GLFWwindow* w, int* width, int* height) { (void)w; if (width) *width = g_reportedWidth; if (height) *height = g_reportedHeight; }
API void glfwSetWindowSizeLimits(GLFWwindow* w, int a, int b, int c, int d) { (void)w; (void)a; (void)b; (void)c; (void)d; }
API void glfwSetWindowAspectRatio(GLFWwindow* w, int n, int d) { (void)w; (void)n; (void)d; }
API void glfwSetWindowSize(GLFWwindow* w, int width, int height) { (void)w; request_render_size(width, height); }
API void glfwGetFramebufferSize(GLFWwindow* w, int* width, int* height) { (void)w; if (width) *width = g_reportedWidth; if (height) *height = g_reportedHeight; }
API void glfwGetWindowFrameSize(GLFWwindow* w, int* l, int* t, int* r, int* b) { (void)w; if (l) *l = 0; if (t) *t = 0; if (r) *r = 0; if (b) *b = 0; }
API void glfwGetWindowContentScale(GLFWwindow* w, float* x, float* y) { (void)w; if (x) *x = 1.0f; if (y) *y = 1.0f; }
API float glfwGetWindowOpacity(GLFWwindow* w) { (void)w; return 1.0f; }
API void glfwSetWindowOpacity(GLFWwindow* w, float o) { (void)w; (void)o; }
API void glfwIconifyWindow(GLFWwindow* w) { (void)w; }
API void glfwRestoreWindow(GLFWwindow* w) { (void)w; }
API void glfwMaximizeWindow(GLFWwindow* w) { (void)w; }
API void glfwShowWindow(GLFWwindow* w) { (void)w; }
API void glfwHideWindow(GLFWwindow* w) { (void)w; }
API void glfwFocusWindow(GLFWwindow* w) { (void)w; }
API void glfwRequestWindowAttention(GLFWwindow* w) { (void)w; }
API GLFWmonitor* glfwGetWindowMonitor(GLFWwindow* w) { (void)w; return &g_monitorObject; }
API void glfwSetWindowMonitor(GLFWwindow* w, GLFWmonitor* m, int x, int y, int width, int height, int rate) { (void)w; (void)m; (void)x; (void)y; (void)rate; request_render_size(width, height); }

API int glfwGetWindowAttrib(GLFWwindow* w, int attrib)
{
    (void)w;
    switch (attrib)
    {
    case GLFW_FOCUSED: return g_reportedFocused;
    case GLFW_VISIBLE: return 1;
    case GLFW_HOVERED: return 1;
    case GLFW_MAXIMIZED: return 1;
    case GLFW_ICONIFIED: case GLFW_RESIZABLE: case GLFW_DECORATED: case GLFW_FLOATING: case GLFW_AUTO_ICONIFY:
    case GLFW_TRANSPARENT_FRAMEBUFFER: case GLFW_FOCUS_ON_SHOW: case GLFW_MOUSE_PASSTHROUGH: return 0;
    case GLFW_CLIENT_API: return GLFW_NO_API;
    default: return 0;
    }
}
API void glfwSetWindowAttrib(GLFWwindow* w, int attrib, int value) { (void)w; (void)attrib; (void)value; }
API void glfwSetWindowUserPointer(GLFWwindow* w, void* p) { (void)w; g_windowUser = p; }
API void* glfwGetWindowUserPointer(GLFWwindow* w) { (void)w; return g_windowUser; }

#define SETCB(name, type, var) API type name(GLFWwindow* w, type f) { (void)w; type old = var; var = f; return old; }
SETCB(glfwSetWindowPosCallback, GLFWwindowposfun, cb_pos)
SETCB(glfwSetWindowSizeCallback, GLFWwindowsizefun, cb_size)
SETCB(glfwSetWindowCloseCallback, GLFWwindowclosefun, cb_close)
SETCB(glfwSetWindowRefreshCallback, GLFWwindowrefreshfun, cb_refresh)
SETCB(glfwSetWindowFocusCallback, GLFWwindowfocusfun, cb_focus)
SETCB(glfwSetWindowIconifyCallback, GLFWwindowiconifyfun, cb_iconify)
SETCB(glfwSetWindowMaximizeCallback, GLFWwindowmaximizefun, cb_maximize)
SETCB(glfwSetFramebufferSizeCallback, GLFWframebuffersizefun, cb_fbsize)
SETCB(glfwSetWindowContentScaleCallback, GLFWwindowcontentscalefun, cb_scale)
SETCB(glfwSetMouseButtonCallback, GLFWmousebuttonfun, cb_button)
SETCB(glfwSetCursorPosCallback, GLFWcursorposfun, cb_cursor)
SETCB(glfwSetCursorEnterCallback, GLFWcursorenterfun, cb_enter)
SETCB(glfwSetScrollCallback, GLFWscrollfun, cb_scroll)
SETCB(glfwSetKeyCallback, GLFWkeyfun, cb_key)
SETCB(glfwSetCharCallback, GLFWcharfun, cb_char)
SETCB(glfwSetCharModsCallback, GLFWcharmodsfun, cb_charmods)
SETCB(glfwSetDropCallback, GLFWdropfun, cb_drop)

// Events
API void glfwPollEvents(void) { drain(); }
API void glfwWaitEvents(void)
{
    pthread_mutex_lock(&g_lock);
    while (g_qhead == g_qtail) pthread_cond_wait(&g_cond, &g_lock);
    pthread_mutex_unlock(&g_lock);
    drain();
}
API void glfwWaitEventsTimeout(double timeout)
{
    struct timespec until;
    timespec_after(&until, timeout);
    pthread_mutex_lock(&g_lock);
    while (g_qhead == g_qtail)
    {
        if (pthread_cond_timedwait(&g_cond, &g_lock, &until) == ETIMEDOUT) break;
    }
    pthread_mutex_unlock(&g_lock);
    drain();
}
API void glfwPostEmptyEvent(void)
{
    pthread_mutex_lock(&g_lock);
    pthread_cond_broadcast(&g_cond);
    pthread_mutex_unlock(&g_lock);
}

// Input
API int glfwGetInputMode(GLFWwindow* w, int mode)
{
    (void)w;
    switch (mode)
    {
    case GLFW_CURSOR: return g_cursorMode;
    case GLFW_STICKY_KEYS: return g_stickyKeys;
    case GLFW_STICKY_MOUSE_BUTTONS: return g_stickyButtons;
    case GLFW_LOCK_KEY_MODS: return g_lockKeyMods;
    case GLFW_RAW_MOUSE_MOTION: return g_rawMotion;
    default: set_error(GLFW_INVALID_ENUM, "invalid input mode"); return 0;
    }
}
API void glfwSetInputMode(GLFWwindow* w, int mode, int value)
{
    (void)w;
    switch (mode)
    {
    case GLFW_CURSOR: g_cursorMode = value; break;
    case GLFW_STICKY_KEYS: g_stickyKeys = value; break;
    case GLFW_STICKY_MOUSE_BUTTONS: g_stickyButtons = value; break;
    case GLFW_LOCK_KEY_MODS: g_lockKeyMods = value; break;
    case GLFW_RAW_MOUSE_MOTION: g_rawMotion = value; break;
    default: set_error(GLFW_INVALID_ENUM, "invalid input mode"); break;
    }
}
API int glfwRawMouseMotionSupported(void) { return GLFW_FALSE; }
API const char* glfwGetKeyName(int key, int scancode) { (void)key; (void)scancode; return NULL; }
API int glfwGetKeyScancode(int key) { return key; }
API int glfwGetKey(GLFWwindow* w, int key) { (void)w; return (key >= 0 && key < 512 && g_keys[key]) ? GLFW_PRESS : GLFW_RELEASE; }
API int glfwGetMouseButton(GLFWwindow* w, int button) { (void)w; return (button >= 0 && button < 8 && g_mouseButtons[button]) ? GLFW_PRESS : GLFW_RELEASE; }
API void glfwGetCursorPos(GLFWwindow* w, double* x, double* y) { (void)w; if (x) *x = g_cursorX; if (y) *y = g_cursorY; }
API void glfwSetCursorPos(GLFWwindow* w, double x, double y) { (void)w; g_cursorX = x; g_cursorY = y; }

API GLFWcursor* glfwCreateCursor(const GLFWimage* image, int xhot, int yhot)
{
    (void)image; (void)xhot; (void)yhot;
    if (g_cursorCount >= 64) g_cursorCount = 0;
    g_cursors[g_cursorCount].shape = 0;
    return &g_cursors[g_cursorCount++];
}
API GLFWcursor* glfwCreateStandardCursor(int shape)
{
    if (g_cursorCount >= 64) g_cursorCount = 0;
    g_cursors[g_cursorCount].shape = shape;
    return &g_cursors[g_cursorCount++];
}
API void glfwDestroyCursor(GLFWcursor* c) { (void)c; }
API void glfwSetCursor(GLFWwindow* w, GLFWcursor* c) { (void)w; (void)c; }

// Joysticks: none (an MFi gamepad mapping could come later through GCController).
API int glfwJoystickPresent(int jid) { (void)jid; return GLFW_FALSE; }
API const float* glfwGetJoystickAxes(int jid, int* count) { (void)jid; if (count) *count = 0; return NULL; }
API const unsigned char* glfwGetJoystickButtons(int jid, int* count) { (void)jid; if (count) *count = 0; return NULL; }
API const unsigned char* glfwGetJoystickHats(int jid, int* count) { (void)jid; if (count) *count = 0; return NULL; }
API const char* glfwGetJoystickName(int jid) { (void)jid; return NULL; }
API const char* glfwGetJoystickGUID(int jid) { (void)jid; return NULL; }
API void glfwSetJoystickUserPointer(int jid, void* p) { (void)jid; (void)p; }
API void* glfwGetJoystickUserPointer(int jid) { (void)jid; return NULL; }
API int glfwJoystickIsGamepad(int jid) { (void)jid; return GLFW_FALSE; }
API GLFWjoystickfun glfwSetJoystickCallback(GLFWjoystickfun f) { GLFWjoystickfun old = cb_joystick; cb_joystick = f; return old; }
API int glfwUpdateGamepadMappings(const char* s) { (void)s; return GLFW_TRUE; }
API const char* glfwGetGamepadName(int jid) { (void)jid; return NULL; }
API int glfwGetGamepadState(int jid, GLFWgamepadstate* state) { (void)jid; (void)state; return GLFW_FALSE; }

// Clipboard: UIPasteboard (the general one), read and written on the main thread — dispatch_sync is
// safe here: the main thread never waits on the engine thread that polls. The last text is kept for the
// pointer glfwGetClipboardString returns, as the Android shim does.
API void glfwSetClipboardString(GLFWwindow* w, const char* s)
{
    (void)w;
    free(g_clipboard);
    g_clipboard = s ? strdup(s) : NULL;
    NSString* text = [NSString stringWithUTF8String:s ? s : ""];
    dispatch_async(dispatch_get_main_queue(), ^{
        UIPasteboard.generalPasteboard.string = text;
    });
}
API const char* glfwGetClipboardString(GLFWwindow* w)
{
    (void)w;
    __block NSString* fresh = nil;
    dispatch_sync(dispatch_get_main_queue(), ^{
        fresh = UIPasteboard.generalPasteboard.string;
    });
    if (fresh != nil)
    {
        free(g_clipboard);
        g_clipboard = strdup(fresh.UTF8String ? fresh.UTF8String : "");
    }
    return g_clipboard ? g_clipboard : "";
}

// Time
API double glfwGetTime(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (double)(now.tv_sec - g_start.tv_sec) + (double)(now.tv_nsec - g_start.tv_nsec) / 1e9;
}
API void glfwSetTime(double t) { (void)t; }
API uint64_t glfwGetTimerValue(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (uint64_t)now.tv_sec * 1000000000ULL + (uint64_t)now.tv_nsec;
}
API uint64_t glfwGetTimerFrequency(void) { return 1000000000ULL; }

// OpenGL: not available.
API void glfwMakeContextCurrent(GLFWwindow* w) { (void)w; }
API GLFWwindow* glfwGetCurrentContext(void) { return NULL; }
API void glfwSwapBuffers(GLFWwindow* w) { (void)w; }
API void glfwSwapInterval(int i) { (void)i; }
API int glfwExtensionSupported(const char* e) { (void)e; return GLFW_FALSE; }
API GLFWglproc glfwGetProcAddress(const char* name) { (void)name; return NULL; }

// Vulkan
static int load_vulkan(void)
{
    if (g_vulkan) return 1;
    // Our own libvulkan.dylib (the forwarder over MoltenVK), by full path: the dylibs sit side by side
    // in the app bundle.
    Dl_info self;
    char path[512];
    if (dladdr((void*)&load_vulkan, &self) && self.dli_fname)
    {
        snprintf(path, sizeof path, "%s", self.dli_fname);
        char* slash = strrchr(path, '/');
        if (slash) { snprintf(slash + 1, sizeof path - (slash + 1 - path), "libvulkan.dylib"); g_vulkan = dlopen(path, RTLD_NOW | RTLD_LOCAL); }
    }
    if (!g_vulkan) g_vulkan = dlopen("libvulkan.dylib", RTLD_NOW | RTLD_LOCAL);
    if (!g_vulkan)
    {
        log_line("libvulkan.dylib: %s", dlerror());
        return 0;
    }
    g_vkGetInstanceProcAddr = (PFN_vkGetInstanceProcAddr)dlsym(g_vulkan, "vkGetInstanceProcAddr");
    return g_vkGetInstanceProcAddr != NULL;
}

API int glfwVulkanSupported(void) { return load_vulkan() ? GLFW_TRUE : GLFW_FALSE; }

API const char** glfwGetRequiredInstanceExtensions(uint32_t* count)
{
    static const char* extensions[] = { "VK_KHR_surface", "VK_EXT_metal_surface" };
    if (!load_vulkan())
    {
        set_error(GLFW_API_UNAVAILABLE, "Vulkan is not available");
        if (count) *count = 0;
        return NULL;
    }
    if (count) *count = 2;
    return extensions;
}

API GLFWvkproc glfwGetInstanceProcAddress(VkInstance instance, const char* name)
{
    if (!load_vulkan()) return NULL;
    return (GLFWvkproc)g_vkGetInstanceProcAddr(instance, name);
}

API int glfwGetPhysicalDevicePresentationSupport(VkInstance instance, VkPhysicalDevice device, uint32_t family)
{
    (void)instance; (void)device; (void)family;
    return GLFW_TRUE;
}

API VkResult glfwCreateWindowSurface(VkInstance instance, GLFWwindow* w, const VkAllocationCallbacks* allocator, VkSurfaceKHR* surface)
{
    (void)w;
    if (surface) *surface = VK_NULL_HANDLE;
    if (!load_vulkan())
    {
        set_error(GLFW_API_UNAVAILABLE, "Vulkan is not available");
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    PFN_vkCreateMetalSurfaceEXT create = (PFN_vkCreateMetalSurfaceEXT)g_vkGetInstanceProcAddr(instance, "vkCreateMetalSurfaceEXT");
    if (!create)
    {
        set_error(GLFW_API_UNAVAILABLE, "vkCreateMetalSurfaceEXT is missing (VK_EXT_metal_surface not enabled)");
        return VK_ERROR_EXTENSION_NOT_PRESENT;
    }
    pthread_mutex_lock(&g_lock);
    int have = g_windowVisible;
    CAMetalLayer* layer = g_layer;
    pthread_mutex_unlock(&g_lock);
    if (!have || !layer)
    {
        set_error(GLFW_PLATFORM_ERROR, "the app has no window to create a surface on");
        return VK_ERROR_SURFACE_LOST_KHR;
    }
    VkMetalSurfaceCreateInfoEXT info;
    memset(&info, 0, sizeof info);
    info.sType = VK_STRUCTURE_TYPE_METAL_SURFACE_CREATE_INFO_EXT;
    info.pLayer = layer;
    VkResult result = create(instance, &info, allocator, surface);
    if (result == VK_ERROR_NATIVE_WINDOW_IN_USE_KHR && g_lastSurface != VK_NULL_HANDLE && g_vulkan)
    {
        // The engine's previous surface still holds this same layer (it destroys the old one only after
        // making the new): the forwarder's release entry (the Android interposer's contract) frees it now,
        // and the engine's later destroy of it is a no-op MoltenVK tolerates.
        void (*release)(VkInstance, VkSurfaceKHR, const VkAllocationCallbacks*) =
            (void (*)(VkInstance, VkSurfaceKHR, const VkAllocationCallbacks*))dlsym(g_vulkan, "port17_vulkan_release_surface");
        if (release)
        {
            log_line("layer still held by surface %p; releasing that one and trying again", (void*)g_lastSurface);
            release(instance, g_lastSurface, allocator);
            g_lastSurface = VK_NULL_HANDLE;
            result = create(instance, &info, allocator, surface);
        }
    }
    if (result != VK_SUCCESS)
    {
        char text[128];
        snprintf(text, sizeof text, "vkCreateMetalSurfaceEXT failed: %d", (int)result);
        set_error(GLFW_PLATFORM_ERROR, text);
    }
    else
    {
        log_line("Vulkan surface created on layer %p", (void*)layer);
        g_surfaceCreated = 1;
        g_lastSurface = *surface;
    }
    return result;
}

// Native access: nothing to give.
API void* glfwGetWin32Adapter(GLFWmonitor* m) { (void)m; return NULL; }
API void* glfwGetWin32Monitor(GLFWmonitor* m) { (void)m; return NULL; }
API void* glfwGetWin32Window(GLFWwindow* w) { (void)w; return NULL; }
API void* glfwGetWGLContext(GLFWwindow* w) { (void)w; return NULL; }
API void* glfwGetCocoaMonitor(GLFWmonitor* m) { (void)m; return NULL; }
API void* glfwGetCocoaWindow(GLFWwindow* w) { (void)w; return NULL; }
API void* glfwGetCocoaView(GLFWwindow* w) { (void)w; return NULL; }
API void* glfwGetNSGLContext(GLFWwindow* w) { (void)w; return NULL; }
API void* glfwGetX11Display(void) { return NULL; }
API unsigned long glfwGetX11Adapter(GLFWmonitor* m) { (void)m; return 0; }
API unsigned long glfwGetX11Monitor(GLFWmonitor* m) { (void)m; return 0; }
API unsigned long glfwGetX11Window(GLFWwindow* w) { (void)w; return 0; }
API void glfwSetX11SelectionString(const char* s) { (void)s; }
API const char* glfwGetX11SelectionString(void) { return NULL; }
API void* glfwGetGLXContext(GLFWwindow* w) { (void)w; return NULL; }
API unsigned long glfwGetGLXWindow(GLFWwindow* w) { (void)w; return 0; }
API void* glfwGetWaylandDisplay(void) { return NULL; }
API void* glfwGetWaylandMonitor(GLFWmonitor* m) { (void)m; return NULL; }
API void* glfwGetWaylandWindow(GLFWwindow* w) { (void)w; return NULL; }
API void* glfwGetEGLDisplay(void) { return NULL; }
API void* glfwGetEGLContext(GLFWwindow* w) { (void)w; return NULL; }
API void* glfwGetEGLSurface(GLFWwindow* w) { (void)w; return NULL; }
API int glfwGetOSMesaColorBuffer(GLFWwindow* w, int* a, int* b, int* c, void** d) { (void)w; (void)a; (void)b; (void)c; (void)d; return GLFW_FALSE; }
API int glfwGetOSMesaDepthBuffer(GLFWwindow* w, int* a, int* b, int* c, void** d) { (void)w; (void)a; (void)b; (void)c; (void)d; return GLFW_FALSE; }
API void* glfwGetOSMesaContext(GLFWwindow* w) { (void)w; return NULL; }
