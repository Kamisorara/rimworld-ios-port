// The host's side of the iOS GLFW shim (host/glfw/glfw_ios.m, built as libglfw.dylib): the host app
// hands over its CAMetalLayer, view and keyboard field before anything else; the shim reads the touches,
// key presses and application events the view forwards to it. The GLFW API itself the engine binds by
// name ("glfw" -> libglfw.dylib); the entries below are the host's and the managed layer's side.
#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// glfwIosApplicationEvent(event): the app's lifecycle, from UIApplicationDelegate notifications.
enum {
    GLFWIOS_WILL_RESIGN_ACTIVE = 1,
    GLFWIOS_DID_ENTER_BACKGROUND = 2,
    GLFWIOS_WILL_ENTER_FOREGROUND = 3,
    GLFWIOS_DID_BECOME_ACTIVE = 4,
};

// Installs the shim on the app (main thread): the view's CAMetalLayer (the window), the view itself
// (touch coordinates, safe area) and the hidden text field of the keyboard bridge.
void glfwIosAttach(void* metalLayer, void* view, void* keyboardField);

// The shim's own logging into the host's log system (installed before anything else).
void glfwIosSetLogSink(void (*sink)(const char*));

// The managed bridge's event sink (hooks/IosHost.cs, at StartupHook time): every host event gets a
// side-channel copy for the engine's AndroidPlatform (WAYLITH_BACKEND=Android feeds off that queue,
// beside the GLFW queue PortMobile reads). Field order matches the C# PortIosEvent (natural alignment).
typedef struct Port17IosEvent { int32_t kind; int64_t i0, i1, i2; float f0, f1; int64_t timeMs; } Port17IosEvent;
void glfwIosSetEventSink(void (*sink)(const Port17IosEvent*));

// The view's bounds changed (rotation, stage changes): the window's pixel size is taken again.
void glfwIosViewResized(void);

// The app went to the background / came back (GLFWIOS_* above).
void glfwIosApplicationEvent(int event);

// A finger event from the host view (main thread): type 1 down, 2 move, 3 up, 4 cancel; x/y in points.
void glfwIosTouch(int type, int id, float x, float y);

// A hardware key press (main thread): a GLFW key code (main.m maps UIKey.keyCode, a USB HID usage) and
// the GLFW modifier mask.
void glfwIosKey(int glfwKey, int action, int mods);

// One character (from a hardware keyboard's UIKey.characters, or the managed layer).
void glfwIosChar(unsigned int codepoint, int mods);

// Text the soft keyboard committed (already whole code points; one char event each).
void glfwIosTextCommit(const unsigned int* codepoints, int count);

// The soft keyboard's action key (Done / enter).
void glfwIosEditorAction(void);

// A backspace from the soft keyboard (seen as an empty replacement by the field's delegate).
void glfwIosBackspace(void);

// The raw touch queue the managed layer (PortMobile) reads; the shape of the Android shim's TouchEvent.
typedef struct Port17TouchEvent { int type; int id; float x, y; double time; } Port17TouchEvent;

// The managed touch layer's API (kept under the Android names the managed code P/Invokes; "glfw"):
int  glfwAndroidPollTouches(Port17TouchEvent* out, int max);
void glfwAndroidSetTouchMode(int managed);
void glfwAndroidInjectCursor(double x, double y);
void glfwAndroidInjectButton(int button, int action, int mods);
void glfwAndroidInjectScroll(double dx, double dy);
void glfwAndroidInjectKey(int key, int action, int mods);
void glfwAndroidInjectChar(unsigned int codepoint, int mods);
float glfwAndroidGetDensity(void);
void glfwAndroidViewSize(int* width, int* height);
int  glfwAndroidCutout(char* out, int size);
// Documents under the app's home, UTF-8 with a terminator; returns its length, 0 when it does not fit.
int  glfwAndroidFilesDir(char* out, int size);
void glfwAndroidShowKeyboard(int show);
void glfwAndroidKeyboardForced(int forced);
void glfwAndroidLastDevices(int* mouse, int* key);
int  glfwAndroidInputDevices(char* out, int size);
int  glfwAndroidPickFiles(int folder);
int  glfwAndroidOpenUrl(const char* url);
int  glfwAndroidOpenFolder(const char* path);
int  glfwAndroidScanMedia(const char* path);
int  glfwAndroidProxyFor(const char* url, char* out, int size);
int  glfwAndroidPerfHint(int mainTid, int renderTid, long long targetNanos);
int  glfwAndroidPerfHintConfig(int mainTid, int renderTid, long long targetNanos, int flags);
void glfwAndroidPerfReport(long long actualNanos);
int  glfwAndroidSetFrameRate(float framesPerSecond);
float glfwAndroidDisplayRate(void);
void glfwAndroidPrepareExit(void);

#ifdef __cplusplus
}
#endif
