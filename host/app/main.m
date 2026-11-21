// The M2 game host, grown from the M1 probe app: a full-screen CAMetalLayer (the window the GLFW shim
// hands the engine), raw touch capture (every finger, as on Android), the soft-keyboard bridge (a
// hidden UITextField — the iOS TextInputView), and a collapsible log overlay. The diagnostics of M1
// stay: the .NET probe (host.c) runs from a button, the Documents logs survive crashes, the stderr of
// the whole process goes to a file.
//
// The triangle (host/app/triangle.c) starts on its own thread at launch: it draws through the shim and
// MoltenVK, which is M2's proof before the engine itself comes.
#import <UIKit/UIKit.h>
#import <QuartzCore/QuartzCore.h>
#import <Metal/Metal.h>
#include <dlfcn.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <pthread.h>
#import "host.h"
#import "../glfw/glfw_ios.h"

int triangle_main(void);
int audio_test(void);
static int run_probe_thread(void);

static UITextView *g_text = nil;

// UI 之外的地面真相：所有阶段打点同步落 Documents/port17-ui.log，黑屏/闪退时经 Files app
// 也能拿到（Info.plist 开了文件共享）。M1 的规矩原样带过来。
static void boot_log(NSString *line)
{
    NSString *path = [NSHomeDirectory()
        stringByAppendingPathComponent:@"Documents/port17-ui.log"];
    NSDate *now = [NSDate date];
    NSDateFormatter *fmt = [[NSDateFormatter alloc] init];
    fmt.dateFormat = @"HH:mm:ss.SSS";
    NSString *out = [NSString stringWithFormat:@"[%@] %@\n",
                            [fmt stringFromDate:now], line];
    FILE *f = fopen(path.fileSystemRepresentation, "a");
    if (f != NULL) {
        fputs(out.UTF8String, f);
        fclose(f);
    }
}

static void append_log(const char *msg)
{
    boot_log([NSString stringWithUTF8String:msg]);
    NSString *line = [NSString stringWithUTF8String:msg];
    dispatch_async(dispatch_get_main_queue(), ^{
        g_text.text = [g_text.text stringByAppendingString:
                              [line stringByAppendingString:@"\n"]];
        NSRange end = { g_text.text.length - 1, 1 };
        [g_text scrollRangeToVisible:end];
    });
}

// triangle.c calls host_log — host.c defines it (routed through host_set_log_sink, which the app
// delegate installs as append_log below); no definition here, or the link sees it twice.

// ---- the window: a CAMetalLayer the whole screen ----

// A USB HID usage (UIKey.keyCode) -> a GLFW key code. The contiguous blocks first, then the singles;
// the values are glfw3.h's (docs/ENGINE-NATIVE-CONTRACT.md keeps the table).
static int key_from_hid(int code)
{
    if (code >= 0x04 && code <= 0x1D) return 'A' + (code - 0x04);           // A-Z
    if (code >= 0x1E && code <= 0x27) return '0' + (code - 0x1E);           // 0-9
    if (code >= 0x3A && code <= 0x45) return 290 + (code - 0x3A);           // F1-F12
    if (code >= 0x59 && code <= 0x61) return 321 + (code - 0x59);           // keypad 1-9
    if (code == 0x62) return 320;                                           // keypad 0
    if (code >= 0xE0 && code <= 0xE7)
    {
        // HID 0xE0 L-Ctrl, 0xE1 L-Shift, 0xE2 L-Alt, 0xE3 L-GUI, 0xE4 R-Ctrl, 0xE5 R-Shift, 0xE6 R-Alt,
        // 0xE7 R-GUI; GLFW L-Shift 340, L-Control 341, L-Alt 342, L-Meta 343, R-Shift 344 …
        static const int modifiers[8] = { 341, 340, 342, 343, 345, 344, 346, 347 };
        return modifiers[code - 0xE0];
    }
    switch (code)
    {
    case 0x28: return 257;  // return
    case 0x29: return 256;  // escape
    case 0x2A: return 259;  // backspace
    case 0x2B: return 258;  // tab
    case 0x2C: return 32;   // space
    case 0x2D: return 45;   // -
    case 0x2E: return 61;   // =
    case 0x2F: return 91;   // [
    case 0x30: return 93;   // ]
    case 0x31: return 92;   // backslash
    case 0x33: return 59;   // ;
    case 0x34: return 39;   // '
    case 0x35: return 96;   // `
    case 0x36: return 44;   // ,
    case 0x37: return 46;   // .
    case 0x38: return 47;   // /
    case 0x39: return 280;  // caps lock
    case 0x46: return 283;  // print screen (sysrq)
    case 0x47: return 281;  // scroll lock
    case 0x48: return 284;  // pause (break)
    case 0x49: return 260;  // insert
    case 0x4A: return 268;  // home
    case 0x4B: return 266;  // page up
    case 0x4C: return 261;  // delete
    case 0x4D: return 269;  // end
    case 0x4E: return 267;  // page down
    case 0x4F: return 262;  // right
    case 0x50: return 263;  // left
    case 0x51: return 264;  // down
    case 0x52: return 265;  // up
    case 0x53: return 282;  // keypad num lock
    case 0x54: return 331;  // keypad /
    case 0x55: return 332;  // keypad *
    case 0x56: return 333;  // keypad -
    case 0x57: return 334;  // keypad +
    case 0x58: return 335;  // keypad enter
    case 0x63: return 330;  // keypad .
    case 0x67: return 336;  // keypad =
    case 0x65: return 348;  // application / menu
    default: return -1;
    }
}

static int mods_from_flags(UIKeyModifierFlags flags)
{
    int mods = 0;
    if (flags & UIKeyModifierShift) mods |= 0x0001;
    if (flags & UIKeyModifierControl) mods |= 0x0002;
    if (flags & UIKeyModifierAlternate) mods |= 0x0004;
    if (flags & UIKeyModifierCommand) mods |= 0x0008;
    if (flags & UIKeyModifierAlphaShift) mods |= 0x0010;
    return mods;
}

// Every finger of every phase goes to the shim as its raw event (the id is the UITouch object's
// address: stable for the finger's life, as Android's pointer id is; the double cast keeps ARC's
// pointer-introspection warning away).
@interface MetalView : UIView
@end

@implementation MetalView

+ (Class)layerClass { return [CAMetalLayer class]; }

- (void)commonInit
{
    self.multipleTouchEnabled = YES;
    self.exclusiveTouch = NO;
    self.backgroundColor = UIColor.blackColor;
}

- (instancetype)initWithFrame:(CGRect)frame
{
    self = [super initWithFrame:frame];
    if (self) [self commonInit];
    return self;
}

- (void)awakeFromNib
{
    [super awakeFromNib];
    [self commonInit];
}

static int touch_type(NSSet<UITouch *> *touches)
{
    // One phase per event (began/moved/ended/cancelled all carry one phase for their set).
    UITouchPhase phase = [[touches anyObject] phase];
    switch (phase)
    {
    case UITouchPhaseBegan: return 1;
    case UITouchPhaseMoved: return 2;
    case UITouchPhaseStationary: return 2;
    case UITouchPhaseEnded: return 3;
    case UITouchPhaseCancelled: return 4;
    default: return 0;
    }
}

- (void)touchesBegan:(NSSet<UITouch *> *)touches withEvent:(UIEvent *)event { [self forward:touches]; }
- (void)touchesMoved:(NSSet<UITouch *> *)touches withEvent:(UIEvent *)event { [self forward:touches]; }
- (void)touchesEnded:(NSSet<UITouch *> *)touches withEvent:(UIEvent *)event { [self forward:touches]; }
- (void)touchesCancelled:(NSSet<UITouch *> *)touches withEvent:(UIEvent *)event { [self forward:touches]; }

- (void)forward:(NSSet<UITouch *> *)touches
{
    int type = touch_type(touches);
    if (type == 0) return;
    for (UITouch *touch in touches)
    {
        CGPoint p = [touch locationInView:self];
        glfwIosTouch(type, (int)(intptr_t)(__bridge const void *)touch & 0x7fffffff, (float)p.x, (float)p.y);
    }
}

// A hardware keyboard (USB, Bluetooth — or the Magic Keyboard): the shim gets the key and the text.
- (void)pressesBegan:(NSSet<UIPress *> *)presses withEvent:(UIPressesEvent *)event
{
    for (UIPress *press in presses)
    {
        UIKey *key = press.key;
        if (key == nil) continue;
        int glfwKey = key_from_hid((int)key.keyCode);
        int mods = mods_from_flags(key.modifierFlags);
        if (glfwKey >= 0) glfwIosKey(glfwKey, 1, mods);
        // The text, unless a modifier owns the key (shortcuts): one code point of key.characters.
        if (key.characters.length > 0 && (mods & ~0x0010) == 0)
        {
            unichar units[8] = { 0 };
            [key.characters getCharacters:units range:NSMakeRange(0, MIN(key.characters.length, 8))];
            unsigned int cp = units[0];
            if (cp >= 0xD800 && cp <= 0xDBFF && key.characters.length > 1 &&
                units[1] >= 0xDC00 && units[1] <= 0xDFFF)
                cp = 0x10000 + ((cp - 0xD800) << 10) + (units[1] - 0xDC00);
            glfwIosChar(cp, mods);
        }
    }
}

- (void)pressesEnded:(NSSet<UIPress *> *)presses withEvent:(UIPressesEvent *)event
{
    for (UIPress *press in presses)
    {
        if (press.key == nil) continue;
        int glfwKey = key_from_hid((int)press.key.keyCode);
        if (glfwKey >= 0)
            glfwIosKey(glfwKey, 0, mods_from_flags(press.key.modifierFlags));
    }
}

@end

// ---- the soft-keyboard bridge: the iOS TextInputView ----
// A hidden UITextField: glfwAndroidShowKeyboard makes it the first responder, the system keyboard
// comes up over the game, and its delegate turns the typing into the shim's char / backspace / action
// events — exactly what Android's TextInputView gives through its JNI natives.

@interface KeyboardField : UITextField <UITextFieldDelegate>
@end

@implementation KeyboardField

- (instancetype)initWithFrame:(CGRect)frame
{
    self = [super initWithFrame:frame];
    if (self)
    {
        self.delegate = self;
        self.autocorrectionType = UITextAutocorrectionTypeNo;
        self.autocapitalizationType = UITextAutocapitalizationTypeNone;
        self.spellCheckingType = UITextSpellCheckingTypeNo;
        self.keyboardType = UIKeyboardTypeDefault;
        self.returnKeyType = UIReturnKeyDone;
        self.alpha = 0.01;                 // in the hierarchy, invisible
        self.userInteractionEnabled = YES;
        // The Done bar: the editor action the managed side reads as "typing finished" (type 5).
        UIToolbar *bar = [[UIToolbar alloc] initWithFrame:CGRectMake(0, 0, 0, 44)];
        UIBarButtonItem *done = [[UIBarButtonItem alloc] initWithBarButtonSystemItem:UIBarButtonSystemItemDone
                                                                              target:self
                                                                              action:@selector(doneTapped:)];
        UIBarButtonItem *space = [[UIBarButtonItem alloc] initWithBarButtonSystemItem:UIBarButtonSystemItemFlexibleSpace
                                                                               target:nil action:nil];
        bar.items = @[space, done];
        self.inputAccessoryView = bar;
    }
    return self;
}

- (void)doneTapped:(id)sender
{
    glfwIosEditorAction();
    [self resignFirstResponder];
}

- (BOOL)textField:(UITextField *)textField shouldChangeCharactersInRange:(NSRange)range replacementString:(NSString *)string
{
    if (string.length == 0)
    {
        glfwIosBackspace();
        return NO;
    }
    // The text as whole code points (surrogate pairs joined).
    unsigned int codepoints[64];
    int n = 0;
    for (int i = 0; i < (int)string.length && n < 64; i++)
    {
        unsigned int cp = [string characterAtIndex:i];
        if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < (int)string.length)
        {
            unsigned int lo = [string characterAtIndex:(NSUInteger)(i + 1)];
            if (lo >= 0xDC00 && lo <= 0xDFFF)
            {
                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                i++;
            }
        }
        codepoints[n++] = cp;
    }
    glfwIosTextCommit(codepoints, n);
    return NO; // the field stays empty: the game owns the text, not the field
}

- (BOOL)textFieldShouldReturn:(UITextField *)textField
{
    glfwIosEditorAction();
    return NO;
}

@end

// ---- the host's own thread spawner (16 MB, the M1 lesson: GCD queues' ~512 KB stacks die in CoreCLR) ----

static void *thread_run(void *arg)
{
    int (*fn)(void) = (int (*)(void))arg;
    fn();
    return NULL;
}

static void run_on_big_thread(int (*fn)(void))
{
    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setstacksize(&attr, 16u * 1024 * 1024);
    pthread_t tid;
    if (pthread_create(&tid, &attr, thread_run, (void *)fn) != 0)
        append_log("线程创建失败");
    pthread_attr_destroy(&attr);
    pthread_detach(tid);
}

// ---- the view controller ----

@interface HostVC : UIViewController
@property (strong, nonatomic) MetalView *gameView;
@property (strong, nonatomic) KeyboardField *keyboard;
@property (strong, nonatomic) UITextView *log;
@end

@implementation HostVC
{
    BOOL m_triangleRan;
    BOOL m_probeRan;
}

- (void)viewDidLoad
{
    [super viewDidLoad];
    boot_log(@"viewDidLoad");
    self.view.backgroundColor = UIColor.blackColor;

    _gameView = [[MetalView alloc] initWithFrame:CGRectZero];
    _gameView.translatesAutoresizingMaskIntoConstraints = NO;
    [self.view addSubview:_gameView];

    _keyboard = [[KeyboardField alloc] initWithFrame:CGRectMake(8, 4, 120, 30)];
    _keyboard.translatesAutoresizingMaskIntoConstraints = NO;
    [self.view addSubview:_keyboard]; // under everything, never seen

    // The log overlay (M1's window, collapsible — the game gets the screen when it's away).
    g_text = [[UITextView alloc] initWithFrame:CGRectZero];
    g_text.translatesAutoresizingMaskIntoConstraints = NO;
    g_text.editable = NO;
    g_text.selectable = YES;
    g_text.backgroundColor = [UIColor.blackColor colorWithAlphaComponent:0.75];
    g_text.textColor = UIColor.greenColor;
    g_text.font = [UIFont fontWithName:@"Menlo" size:11];
    g_text.layoutManager.allowsNonContiguousLayout = NO;
    g_text.hidden = YES;
    [self.view addSubview:g_text];

    // The buttons of the diagnostics: the triangle (auto-runs), the .NET probe, the log, the keyboard.
    UIButton *tri = [UIButton buttonWithType:UIButtonTypeSystem];
    [tri setTitle:@"三角形" forState:UIControlStateNormal];
    [tri addTarget:self action:@selector(triangleTapped:) forControlEvents:UIControlEventTouchUpInside];
    UIButton *probe = [UIButton buttonWithType:UIButtonTypeSystem];
    [probe setTitle:@".NET 探针" forState:UIControlStateNormal];
    [probe addTarget:self action:@selector(probeTapped:) forControlEvents:UIControlEventTouchUpInside];
    UIButton *logBtn = [UIButton buttonWithType:UIButtonTypeSystem];
    [logBtn setTitle:@"日志" forState:UIControlStateNormal];
    [logBtn addTarget:self action:@selector(logTapped:) forControlEvents:UIControlEventTouchUpInside];
    UIButton *kbBtn = [UIButton buttonWithType:UIButtonTypeSystem];
    [kbBtn setTitle:@"键盘" forState:UIControlStateNormal];
    [kbBtn addTarget:self action:@selector(keyboardTapped:) forControlEvents:UIControlEventTouchUpInside];
    UIButton *audioBtn = [UIButton buttonWithType:UIButtonTypeSystem];
    [audioBtn setTitle:@"音频" forState:UIControlStateNormal];
    [audioBtn addTarget:self action:@selector(audioTapped:) forControlEvents:UIControlEventTouchUpInside];
    UIStackView *bar = [[UIStackView alloc] initWithFrame:CGRectZero];
    bar.translatesAutoresizingMaskIntoConstraints = NO;
    bar.axis = UILayoutConstraintAxisHorizontal;
    bar.spacing = 12;
    for (UIView *v in @[tri, probe, logBtn, kbBtn, audioBtn]) [bar addArrangedSubview:v];

    [self.view addSubview:bar];
    UILayoutGuide *safe = self.view.safeAreaLayoutGuide;
    [NSLayoutConstraint activateConstraints:@[
        [_gameView.topAnchor constraintEqualToAnchor:self.view.topAnchor],
        [_gameView.leadingAnchor constraintEqualToAnchor:self.view.leadingAnchor],
        [_gameView.trailingAnchor constraintEqualToAnchor:self.view.trailingAnchor],
        [_gameView.bottomAnchor constraintEqualToAnchor:self.view.bottomAnchor],
        [_keyboard.topAnchor constraintEqualToAnchor:safe.topAnchor],
        [_keyboard.leadingAnchor constraintEqualToAnchor:safe.leadingAnchor],
        [bar.topAnchor constraintEqualToAnchor:safe.topAnchor],
        [bar.centerXAnchor constraintEqualToAnchor:safe.centerXAnchor],
        [g_text.topAnchor constraintEqualToAnchor:bar.bottomAnchor constant:4],
        [g_text.leadingAnchor constraintEqualToAnchor:safe.leadingAnchor],
        [g_text.trailingAnchor constraintEqualToAnchor:safe.trailingAnchor],
        [g_text.heightAnchor constraintEqualToAnchor:safe.heightAnchor multiplier:0.32],
    ]];

    // The shim is attached before anything asks it for a window (the Android host's attach_glfw rule:
    // it must see the window and the input before the engine starts). Its logging and the Vulkan
    // forwarder's — and the audio's and the allocator's — go to the same overlay.
    glfwIosSetLogSink(append_log);
    for (NSString *name in @[@"libvulkan.dylib", @"libwaylithaudio.dylib", @"libvma.dylib"])
    {
        NSString *entry = [name isEqualToString:@"libwaylithaudio.dylib"] ? @"port17_audio_set_log_sink"
                            : [name isEqualToString:@"libvma.dylib"] ? @"port17_vma_set_log_sink"
                            : @"port17_vulkan_set_log_sink";
        void *lib = dlopen([[[NSBundle mainBundle].bundlePath
            stringByAppendingPathComponent:name] fileSystemRepresentation], RTLD_NOW | RTLD_LOCAL);
        if (lib)
        {
            void (*sink)(void (*)(const char *)) =
                (void (*)(void (*)(const char *)))dlsym(lib, entry.UTF8String);
            if (sink) sink(append_log);
        }
    }
    CAMetalLayer *layer = (CAMetalLayer *)_gameView.layer;
    layer.device = MTLCreateSystemDefaultDevice();
    layer.pixelFormat = MTLPixelFormatBGRA8Unorm;
    layer.framebufferOnly = YES; // present only, no reads: the fastest path, what a game takes
    glfwIosAttach((__bridge void *)layer, (__bridge void *)_gameView, (__bridge void *)_keyboard);

    // 启动即回放既有 ui 日志：上一次会话（含抢救出的 stderr 尾部）直接可见（M1 的规矩）。
    NSString *uiPath = [NSHomeDirectory()
        stringByAppendingPathComponent:@"Documents/port17-ui.log"];
    NSString *prev = [NSString stringWithContentsOfFile:uiPath
                                              encoding:NSUTF8StringEncoding
                                                 error:nil];
    if (prev.length > 0) {
        g_text.text = prev;
        NSRange end = { g_text.text.length - 1, 1 };
        [g_text scrollRangeToVisible:end];
    }

    // 前后台：垫片把后台当作窗口消失（fbsize 0x0，引擎当作最小化），回前台再宣布回来。
    NSNotificationCenter *nc = NSNotificationCenter.defaultCenter;
    NSOperationQueue *main = [NSOperationQueue mainQueue];
    [nc addObserverForName:UIApplicationWillResignActiveNotification object:nil queue:main
                  usingBlock:^(NSNotification *note) { (void)note; glfwIosApplicationEvent(GLFWIOS_WILL_RESIGN_ACTIVE); }];
    [nc addObserverForName:UIApplicationDidEnterBackgroundNotification object:nil queue:main
                  usingBlock:^(NSNotification *note) { (void)note; glfwIosApplicationEvent(GLFWIOS_DID_ENTER_BACKGROUND); }];
    [nc addObserverForName:UIApplicationWillEnterForegroundNotification object:nil queue:main
                  usingBlock:^(NSNotification *note) { (void)note; glfwIosApplicationEvent(GLFWIOS_WILL_ENTER_FOREGROUND); }];
    [nc addObserverForName:UIApplicationDidBecomeActiveNotification object:nil queue:main
                  usingBlock:^(NSNotification *note) { (void)note; glfwIosApplicationEvent(GLFWIOS_DID_BECOME_ACTIVE); }];
}

- (void)viewDidAppear:(BOOL)animated
{
    [super viewDidAppear:animated];
    boot_log(@"viewDidAppear");
    if (!m_triangleRan) {
        m_triangleRan = YES;
        append_log("==== Port17 M2 宿主 ====");
        append_log("Vulkan 三角形自动启动（触摸拖顶点；「键盘」打字进日志；Esc/日志按钮切换）");
        run_on_big_thread(triangle_main);
    }
}

- (void)viewDidLayoutSubviews
{
    [super viewDidLayoutSubviews];
    // 布局定了窗口尺寸才有效：attach 时（viewDidLoad）视图还是零尺寸，旋转/Stage
    // 变化也走这里。垫片自己判断尺寸没变就什么都不做。
    glfwIosViewResized();
}

- (void)triangleTapped:(id)sender
{
    if (!m_triangleRan) {
        m_triangleRan = YES;
        run_on_big_thread(triangle_main);
    } else {
        append_log("三角形已在跑（一个实例；重启 app 再来一次）");
    }
}

- (void)probeTapped:(id)sender
{
    if (m_probeRan) {
        // CoreCLR 单实例：进程内二次 coreclr_initialize 会 0x80131022（M1 的已知债）。
        append_log("探针已跑过一次；CoreCLR 单实例，重启 app 才能再跑");
        return;
    }
    m_probeRan = YES;
    run_on_big_thread(run_probe_thread);
}

- (void)logTapped:(id)sender
{
    g_text.hidden = !g_text.hidden;
    if (!g_text.hidden) {
        NSRange end = { g_text.text.length - 1, 1 };
        [g_text scrollRangeToVisible:end];
    }
}

- (void)keyboardTapped:(id)sender
{
    // 直接走垫片的软键盘入口（引擎将来的同一条路）。
    glfwAndroidShowKeyboard(_keyboard.isFirstResponder ? 0 : 1);
}

- (void)audioTapped:(id)sender
{
    // waylithaudio 的 CoreAudio 自检（引擎的 wa_* API 面；结束后可再跑）。
    append_log("音频自检启动（waylithaudio / CoreAudio）");
    run_on_big_thread(audio_test);
}

@end

// The probe on its big thread: stderr redirected first, as M1 does it.
static void *probe_thread(void *unused)
{
    (void)unused;
    boot_log(@"probe 线程启动（16MB 栈）");
    NSString *errPath = [NSHomeDirectory()
        stringByAppendingPathComponent:@"Documents/coreclr-stderr.log"];
    FILE *errFile = freopen(errPath.fileSystemRepresentation, "w", stderr);
    setvbuf(stderr, NULL, _IONBF, 0);
    boot_log(errFile != NULL
        ? @"stderr 已重定向到 Documents/coreclr-stderr.log（无缓冲）"
        : @"stderr 重定向失败（沿用系统日志）");
    fprintf(stderr, "[probe] === 本会话 stderr 捕获自检 ===\n");

    const char *bundle = [[NSBundle mainBundle].bundlePath UTF8String];
    int rc = run_probe(bundle);
    if (rc != -4) {
        NSString *err = [NSString stringWithContentsOfFile:errPath
            encoding:NSUTF8StringEncoding error:nil];
        if (err.length > 0) {
            append_log("---- CoreCLR stderr ----");
            append_log(err.UTF8String);
        }
    }
    char buf[64];
    snprintf(buf, sizeof(buf), "run_probe 返回 %d", rc);
    append_log(buf);
    return NULL;
}

// run_probe wants the plain signature the thread spawner takes.
static int run_probe_thread(void)
{
    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setstacksize(&attr, 16u * 1024 * 1024);
    pthread_t tid;
    if (pthread_create(&tid, &attr, probe_thread, NULL) != 0)
        append_log("probe 线程创建失败");
    pthread_attr_destroy(&attr);
    pthread_detach(tid);
    return 0;
}

@interface AppDelegate : UIResponder <UIApplicationDelegate>
@property (strong, nonatomic) UIWindow *window;
@end

@implementation AppDelegate

- (BOOL)application:(UIApplication *)application
    didFinishLaunchingWithOptions:(NSDictionary *)options
{
    self.window = [[UIWindow alloc]
        initWithFrame:UIScreen.mainScreen.bounds];
    self.window.rootViewController = [[HostVC alloc] init];
    [self.window makeKeyAndVisible];
    host_set_log_sink(append_log);
    return YES;
}

@end

// 崩溃信号：落一行打点 + 把 coreclr-stderr.log 尾部抢救进持久化的 port17-ui.log（M1 的 best-effort），
// 然后交还系统默认处理写 .ips 报告。没有任何信号行而进程消失 = SIGKILL（jetsam/看门狗）。
static void crash_handler(int sig)
{
    char buf[96];
    snprintf(buf, sizeof(buf),
             "收到信号 %d（SIGSEGV=11, SIGBUS=10, SIGABRT=6, SIGTRAP=5, SIGILL=4），转系统写崩溃报告",
             sig);
    boot_log([NSString stringWithUTF8String:buf]);
    NSString *errPath = [NSHomeDirectory()
        stringByAppendingPathComponent:@"Documents/coreclr-stderr.log"];
    NSString *uiPath = [NSHomeDirectory()
        stringByAppendingPathComponent:@"Documents/port17-ui.log"];
    NSFileHandle *fh = [NSFileHandle fileHandleForReadingAtPath:errPath];
    if (fh != nil) {
        unsigned long long len = [fh seekToEndOfFile];
        unsigned long long tail = len > 8192 ? 8192 : len;
        [fh seekToFileOffset:len - tail];
        NSData *data = [fh readDataToEndOfFile];
        [fh closeFile];
        NSString *tailStr = [[NSString alloc] initWithData:data
                                                  encoding:NSUTF8StringEncoding];
        if (tailStr.length > 0) {
            NSString *out = [NSString stringWithFormat:
                @"---- 崩溃时 stderr 尾部（抢救）----\n%@\n---- 抢救完毕 ----\n", tailStr];
            FILE *f = fopen(uiPath.fileSystemRepresentation, "a");
            if (f != NULL) {
                fputs(out.UTF8String, f);
                fclose(f);
            }
        }
    }
    signal(sig, SIG_DFL);
    raise(sig);
}

int main(int argc, char *argv[])
{
    @autoreleasepool {
        boot_log(@"==== main 进入（新会话）====");
        NSString *errPath = [NSHomeDirectory()
            stringByAppendingPathComponent:@"Documents/coreclr-stderr.log"];
        freopen(errPath.fileSystemRepresentation, "w", stderr);
        setvbuf(stderr, NULL, _IONBF, 0);
        NSFileManager *fm = [NSFileManager defaultManager];
        NSString *docDir = [NSHomeDirectory()
            stringByAppendingPathComponent:@"Documents"];
        BOOL isDir = NO;
        BOOL docOk = [fm fileExistsAtPath:docDir isDirectory:&isDir] && isDir;
        boot_log(docOk ? @"Documents 目录存在（Files app：浏览 → 我的 iPhone → Port17）"
                       : @"Documents 目录缺失（沙盒异常，日志只剩屏幕一份）");
        signal(SIGSEGV, crash_handler);
        signal(SIGBUS, crash_handler);
        signal(SIGABRT, crash_handler);
        signal(SIGTRAP, crash_handler);
        signal(SIGILL, crash_handler);
        setenv("HELLO_OUT", [[NSHomeDirectory()
            stringByAppendingPathComponent:@"Documents/hello.log"]
            fileSystemRepresentation], 1);
        // MoltenVK 稳定性旋钮（MHGURecomp-iOS 的实战教训，M2/iOS18 起 residency sets 不稳）：
        // 必须在 MoltenVK 装载前设置——它读环境变量的时机是实例创建。用户设备 M4/iOS27 属于
        // 同一风险面，关掉只影响性能优化路径，不影响功能。
        setenv("MVK_CONFIG_RESIDENCY_SETS", "0", 0);
        setenv("MVK_CONFIG_USE_MTLRESIDENCYSETS", "0", 0);
        boot_log(@"UIApplicationMain 开始");
        int rc = UIApplicationMain(argc, argv, nil,
                                   NSStringFromClass([AppDelegate class]));
        boot_log(@"UIApplicationMain 返回");
        return rc;
    }
}
