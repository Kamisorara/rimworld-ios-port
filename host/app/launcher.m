// 启动器：宿主诊断界面的 s2arm 风格改版（参照 s2arm/S2Arm/Engine/ios/notice.m 与
// loadpage.m 的视觉语言——游戏图标放大模糊铺底、圆角图标 + 品牌色状态角标、居中窄列、
// 胶囊按钮），外加状态卡片：执行模式（JIT/解释器）、setup v9、mod 数、上次引擎会话、
// 本次实况——裁决链的信号直接在屏上，不再只埋在 port17-ui.log 里。
// iOS 26+ 的卡片走 UIGlassEffect（液态玻璃）；旧系统回退 UIBlurEffect。
// main.m 保留生命周期与启动决策；这里的日志地基（boot_log / append_log / sinks）
// 由 main() 在 UIApplicationMain 之前就装好——引擎模式下宿主 UIKit 未起，日志先落盘。
#import "launcher.h"
#import "host.h"
#import "unzip.h"
#import "status.h"
#import "jit26.h"
#import "../glfw/glfw_ios.h"
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#import <QuartzCore/QuartzCore.h>
#import <Metal/Metal.h>
#include <dlfcn.h>
#include <string.h>
#include <stdio.h>
#include <pthread.h>

int triangle_main(void);
int audio_test(void);
int libs_check(void);

// 当前活着的启动器实例（状态行到达时刷卡片；nil = 界面不在，只落仓）。
static __weak LauncherVC *g_live = nil;

// extension 在这两个类的 @interface 之前（属性类型要用）——前向声明。
@class MetalView;
@class KeyboardField;

@interface LauncherVC ()
@property (strong, nonatomic) MetalView *gameView;
@property (strong, nonatomic) KeyboardField *keyboard;
@property (strong, nonatomic) UITextView *log;
@property (strong, nonatomic) UIView *badgeDot;            // 角标圆点（状态符号）
@property (strong, nonatomic) NSArray<UIVisualEffectView *> *cards;
@property (strong, nonatomic) NSArray<UILabel *> *cardValues;
@property (strong, nonatomic) NSArray<UIView *> *cardDots;
@property (strong, nonatomic) UIButton *diagBtn;
@property (strong, nonatomic) NSLayoutConstraint *colWidthConstraint;
@property (nonatomic) BOOL triangleRan;
@property (nonatomic) BOOL probeRan;
@property (nonatomic) BOOL engineRan;
- (void)refreshCards;
@end

// ---- 日志地基（原 main.m 的 M1 规矩原样带过来）----

static UITextView *g_text = nil;

void boot_log(NSString *line)
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

static void status_store_set(const char *key, const char *value)
{
    port17_status_set(NSHomeDirectory().UTF8String, key, value);
}

// [port17-status] 行入口：解析 "key=value"，落仓 + 主队列刷卡片。
void port17_launcher_status_line(const char *line)
{
    const char *eq = strchr(line, '=');
    if (eq == NULL || eq == line)
        return;
    char key[64], value[448];
    size_t klen = (size_t)(eq - line);
    if (klen >= sizeof key)
        klen = sizeof key - 1;
    memcpy(key, line, klen);
    key[klen] = '\0';
    snprintf(value, sizeof value, "%s", eq + 1);
    status_store_set(key, value);
    dispatch_async(dispatch_get_main_queue(), ^{
        if (g_live)
            [g_live refreshCards];
    });
}

void append_log(const char *msg)
{
    // managed 的状态行（PortMobile.Entry）：进日志留档，同时转状态仓 + 刷新卡片。
    if (strncmp(msg, "[port17-status] ", 16) == 0)
        port17_launcher_status_line(msg + 16);
    // setup 的收官行带改写计数（Port17Setup 的 Say 原文）：折进 setup_last，
    // 比宿主侧只知道 rc 的粗粒度信息有用（成功路径只有这里有计数）。
    else if (strncmp(msg, "[setup] done: ", 14) == 0)
        status_store_set("setup_last", msg + 14);
    boot_log([NSString stringWithUTF8String:msg]);
    NSString *line = [NSString stringWithUTF8String:msg];
    dispatch_async(dispatch_get_main_queue(), ^{
        // 引擎把 stdout 整个泵进来，覆盖层不能无界增长：留尾部的量（port17-ui.log 有全文）。
        if (g_text.text.length > 200000)
            g_text.text = [g_text.text substringFromIndex:g_text.text.length - 100000];
        g_text.text = [g_text.text stringByAppendingString:
                              [line stringByAppendingString:@"\n"]];
        NSRange end = { g_text.text.length - 1, 1 };
        [g_text scrollRangeToVisible:end];
    });
}

void port17_ui_install_sinks(void)
{
    static BOOL installed = NO;
    if (installed)
        return;
    installed = YES;
    host_set_log_sink(append_log);
    glfwIosSetLogSink(append_log); // 垫片自身的日志也走同一地面真相（原 viewDidLoad 的位置早于 attach）
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
}

// 诊断保持开关（main() 的启动判定与下面的切换按钮共用；原 main.m 的注释照搬）：
// present = next launch skips engine mode AND setup, straight to the diagnostics screen
// (repeated import/export/debug rounds without the game taking the start).
NSString *port17_diag_hold_path(void)
{
    return [NSHomeDirectory() stringByAppendingPathComponent:@"Documents/port17-diag.on"];
}

// setup 用掉本进程 CoreCLR 的标记（main() 的 setup 分支打，引擎按钮读）。
static BOOL g_clrSpentBySetup = NO;
void port17_launcher_note_clr_spent(void) { g_clrSpentBySetup = YES; }

// ---- 宿主自己的线程 spawner（16 MB，M1 的教训：GCD 队列的 ~512 KB 栈会死在 CoreCLR）----

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

// probe 的大线程体（原 main.m 原样）：stderr 先重定向，再上 CoreCLR。
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

static int engine_thread(void)
{
    boot_log(@"engine 线程启动（16MB 栈）");
    const char *bundle = [[NSBundle mainBundle].bundlePath UTF8String];
    const char *home = [NSHomeDirectory() UTF8String];
    port17_install_game_zip(home); // 诊断模式里丢进来的 zip 也装上（装完再判定）
    int rc = run_engine(bundle, home);
    char buf[64];
    snprintf(buf, sizeof(buf), "run_engine 返回 %d", rc);
    append_log(buf);
    // 上次会话留档（启动器「上次引擎会话」卡片的数据源；崩溃另有信号打点）
    char s[64];
    snprintf(s, sizeof(s), "诊断会话 rc %d", rc);
    status_store_set("session_last", s);
    return rc;
}

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

// ---- 输入转发（原 main.m 原样）：USB HID -> GLFW，触点 -> 垫片 ----

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
    return NO;
}

- (BOOL)textFieldShouldReturn:(UITextField *)textField
{
    glfwIosEditorAction();
    return NO;
}

@end

// ---- 启动器视觉常量与小件（s2arm notice.m 的同款件）----

// 品牌色：RimWorld 琥珀金（对齐 s2arm 的 s2a_notice_accent 单点定义，一行可换）。
static UIColor *port17_accent(void)
{
    return [UIColor colorWithRed:0.91 green:0.64 blue:0.24 alpha:1];
}

static UILabel *launcher_label(CGFloat size, UIFontWeight w, UIColor *c)
{
    UILabel *l = [UILabel new];
    l.font = [UIFont systemFontOfSize:size weight:w];
    l.textColor = c;
    l.numberOfLines = 0;
    l.translatesAutoresizingMaskIntoConstraints = NO;
    return l;
}

// 玻璃卡：iOS 26 走 UIGlassEffect（液态玻璃），旧系统回退系统材质模糊。
// UIGlassEffectStyle 枚举名以 CI 的 SDK 头文件为准（init(style:) 与 isInteractive
// 已按 Apple 文档核实；编译报错就调这一处）。
static UIVisualEffectView *port17_glass(CGFloat radius)
{
    UIVisualEffect *effect;
    if (@available(iOS 26.0, *)) {
        UIGlassEffect *glass = [[UIGlassEffect alloc] initWithStyle:UIGlassEffectStyleRegular];
        glass.isInteractive = NO;
        effect = glass;
    } else {
        effect = [UIBlurEffect effectWithStyle:UIBlurEffectStyleSystemMaterial];
    }
    UIVisualEffectView *v = [[UIVisualEffectView alloc] initWithEffect:effect];
    v.layer.cornerRadius = radius;
    v.layer.cornerCurve = kCACornerCurveContinuous;
    v.translatesAutoresizingMaskIntoConstraints = NO;
    return v;
}

// 胶囊按钮（s2arm notice.m 的 s2a_notice_button 同款）。
static UIButton *launcher_button(NSString *title, BOOL primary, void (^tap)(UIButton *b))
{
    UIButtonConfiguration *c = [UIButtonConfiguration filledButtonConfiguration];
    c.title = title;
    c.baseBackgroundColor = primary ? port17_accent() : [UIColor colorWithWhite:1 alpha:0.14];
    c.baseForegroundColor = primary ? [UIColor colorWithWhite:0.1 alpha:1] : UIColor.whiteColor;
    c.cornerStyle = UIButtonConfigurationCornerStyleCapsule;
    c.contentInsets = NSDirectionalEdgeInsetsMake(10, 20, 10, 20);
    c.titleTextAttributesTransformer = ^NSDictionary *(NSDictionary *a) {
        NSMutableDictionary *m = a.mutableCopy;
        m[NSFontAttributeName] = [UIFont systemFontOfSize:15 weight:UIFontWeightSemibold];
        return m;
    };
    return [UIButton buttonWithConfiguration:c primaryAction:[UIAction actionWithHandler:^(UIAction *a) { tap((UIButton *)a.sender); }]];
}

@implementation LauncherVC

- (void)viewDidLoad
{
    [super viewDidLoad];
    boot_log(@"viewDidLoad（启动器）");
    self.view.backgroundColor = UIColor.blackColor;
    self.overrideUserInterfaceStyle = UIUserInterfaceStyleDark;

    _gameView = [[MetalView alloc] initWithFrame:CGRectZero];
    _gameView.translatesAutoresizingMaskIntoConstraints = NO;
    [self.view addSubview:_gameView];

    _keyboard = [[KeyboardField alloc] initWithFrame:CGRectMake(8, 4, 120, 30)];
    _keyboard.translatesAutoresizingMaskIntoConstraints = NO;
    [self.view addSubview:_keyboard]; // under everything, never seen

    // ---- 氛围底：App 图标放大铺满 + 强模糊压暗（s2arm 同款，只给颜色不抢内容）----
    UIImage *icon = [UIImage imageNamed:@"AppIcon60x60@3x"] ?:
                     [UIImage imageNamed:@"AppIcon60x60@2x"];
    UIImageView *backdrop = [[UIImageView alloc] initWithImage:icon];
    backdrop.contentMode = UIViewContentModeScaleAspectFill;
    backdrop.clipsToBounds = YES;
    backdrop.alpha = 0.9;
    backdrop.translatesAutoresizingMaskIntoConstraints = NO;
    [self.view addSubview:backdrop];
    UIVisualEffectView *backdropBlur = [[UIVisualEffectView alloc]
        initWithEffect:[UIBlurEffect effectWithStyle:UIBlurEffectStyleSystemMaterialDark]];
    backdropBlur.translatesAutoresizingMaskIntoConstraints = NO;
    [self.view addSubview:backdropBlur];

    // 日志覆盖层（可收起；样式从绿底黑字升级为深玻璃 + 浅色 Menlo，游戏获得屏幕时隐藏）。
    g_text = [[UITextView alloc] initWithFrame:CGRectZero];
    g_text.translatesAutoresizingMaskIntoConstraints = NO;
    g_text.editable = NO;
    g_text.selectable = YES;
    g_text.backgroundColor = [UIColor.blackColor colorWithAlphaComponent:0.72];
    g_text.textColor = [UIColor colorWithWhite:0.92 alpha:1];
    g_text.font = [UIFont fontWithName:@"Menlo" size:11];
    g_text.layer.cornerRadius = 12;
    g_text.layoutManager.allowsNonContiguousLayout = NO;
    g_text.hidden = YES;
    g_text.layer.zPosition = 500;
    [self.view addSubview:g_text];

    UILayoutGuide *safe = self.view.safeAreaLayoutGuide;
    UIScrollView *scroll = [UIScrollView new];
    scroll.translatesAutoresizingMaskIntoConstraints = NO;
    scroll.alwaysBounceVertical = YES;
    [self.view addSubview:scroll];
    UIView *col = [UIView new];
    col.translatesAutoresizingMaskIntoConstraints = NO;
    [scroll addSubview:col];

    // ---- 头部：圆角图标 + 状态角标 + 标题/版本（s2arm badge_icon 同款）----
    UIImageView *iconView = [[UIImageView alloc] initWithImage:icon];
    iconView.translatesAutoresizingMaskIntoConstraints = NO;
    iconView.contentMode = UIViewContentModeScaleAspectFill;
    iconView.layer.cornerRadius = 22;
    iconView.layer.cornerCurve = kCACornerCurveContinuous;
    iconView.layer.masksToBounds = YES;
    iconView.layer.shadowColor = UIColor.blackColor.CGColor;
    iconView.layer.shadowOpacity = 0.35;
    iconView.layer.shadowRadius = 12;
    iconView.layer.shadowOffset = CGSizeMake(0, 6);
    _badgeDot = [UIView new];
    _badgeDot.translatesAutoresizingMaskIntoConstraints = NO;
    _badgeDot.backgroundColor = port17_accent();
    _badgeDot.layer.cornerRadius = 16;
    _badgeDot.layer.borderWidth = 2.5;
    _badgeDot.layer.borderColor = [UIColor colorWithWhite:0.08 alpha:1].CGColor;
    UIImageView *badgeSym = [[UIImageView alloc] initWithImage:
        [UIImage systemImageNamed:@"tray.fill" withConfiguration:
            [UIImageSymbolConfiguration configurationWithPointSize:14 weight:UIImageSymbolWeightBold]]];
    badgeSym.tintColor = UIColor.whiteColor;
    badgeSym.translatesAutoresizingMaskIntoConstraints = NO;
    [_badgeDot addSubview:badgeSym];
    [self.view addSubview:iconView]; // 挂根视图：阴影不被模糊层裁掉，布局仍对齐列
    [self.view addSubview:_badgeDot];

    UILabel *title = launcher_label(24, UIFontWeightBold, UIColor.labelColor);
    title.text = @"Port17";
    title.textAlignment = NSTextAlignmentCenter;
    UILabel *subtitle = launcher_label(13, UIFontWeightRegular, UIColor.secondaryLabelColor);
    subtitle.font = [UIFont monospacedDigitSystemFontOfSize:13 weight:UIFontWeightRegular];
    subtitle.textAlignment = NSTextAlignmentCenter;
    subtitle.text = [NSString stringWithFormat:@"v%@ · 启动器",
        [NSBundle mainBundle].infoDictionary[@"CFBundleShortVersionString"] ?: @"?"];

    // ---- 状态卡片（五张：值由 refreshCards 填）----
    NSArray *cardNames = @[@"执行模式（上次会话）", @"Setup v9 改写", @"Mod（上次会话）",
                           @"上次引擎会话", @"本次实况"];
    NSMutableArray *cards = [NSMutableArray array];
    NSMutableArray *values = [NSMutableArray array];
    NSMutableArray *dots = [NSMutableArray array];
    for (NSString *name in cardNames)
    {
        UIVisualEffectView *card = port17_glass(16);
        UILabel *cap = launcher_label(13, UIFontWeightMedium, UIColor.secondaryLabelColor);
        cap.text = name;
        UILabel *val = launcher_label(13, UIFontWeightMedium, UIColor.labelColor);
        val.font = [UIFont monospacedDigitSystemFontOfSize:13 weight:UIFontWeightMedium];
        val.textAlignment = NSTextAlignmentRight;
        val.numberOfLines = 2;
        [val setContentCompressionResistancePriority:UILayoutPriorityDefaultLow
                                             forAxis:UILayoutConstraintAxisHorizontal];
        UIView *dot = [UIView new];
        dot.translatesAutoresizingMaskIntoConstraints = NO;
        dot.layer.cornerRadius = 4;
        dot.backgroundColor = UIColor.tertiaryLabelColor;
        UIStackView *row = [[UIStackView alloc] initWithArrangedSubviews:@[dot, cap, val]];
        row.translatesAutoresizingMaskIntoConstraints = NO;
        row.axis = UILayoutConstraintAxisHorizontal;
        row.spacing = 8;
        row.alignment = UIStackViewAlignmentCenter;
        [card.contentView addSubview:row];
        [NSLayoutConstraint activateConstraints:@[
            [row.topAnchor constraintEqualToAnchor:card.contentView.topAnchor constant:10],
            [row.bottomAnchor constraintEqualToAnchor:card.contentView.bottomAnchor constant:-10],
            [row.leadingAnchor constraintEqualToAnchor:card.contentView.leadingAnchor constant:14],
            [row.trailingAnchor constraintEqualToAnchor:card.contentView.trailingAnchor constant:-14],
            [dot.widthAnchor constraintEqualToConstant:8],
            [dot.heightAnchor constraintEqualToConstant:8],
        ]];
        [cards addObject:card];
        [values addObject:val];
        [dots addObject:dot];
    }
    _cards = cards;
    _cardValues = values;
    _cardDots = dots;

    // ---- 操作按钮：主=启动引擎，次级两行胶囊 ----
    UIButton *engineBtn = launcher_button(@"启动引擎", YES, ^(UIButton *b) { [self engineTapped:b]; });
    UIButton *importBtn = launcher_button(@"导入 zip", NO, ^(UIButton *b) { [self importTapped:b]; });
    UIButton *exportBtn = launcher_button(@"导出", NO, ^(UIButton *b) { [self exportTapped:b]; });
    _diagBtn = launcher_button([LauncherVC diagHoldOn] ? @"下次进引擎" : @"下次诊断", NO, ^(UIButton *b) { [self diagTapped:b]; });
    UIButton *probeBtn = launcher_button(@".NET 探针", NO, ^(UIButton *b) { [self probeTapped:b]; });
    UIButton *triBtn = launcher_button(@"三角形", NO, ^(UIButton *b) { [self triangleTapped:b]; });
    UIButton *kbBtn = launcher_button(@"键盘", NO, ^(UIButton *b) { [self keyboardTapped:b]; });
    UIButton *audioBtn = launcher_button(@"音频", NO, ^(UIButton *b) { [self audioTapped:b]; });
    UIButton *libsBtn = launcher_button(@"库", NO, ^(UIButton *b) { [self libsTapped:b]; });
    UIButton *logBtn = launcher_button(@"日志", NO, ^(UIButton *b) { [self logTapped:b]; });

    UIStackView *row1 = [[UIStackView alloc] initWithArrangedSubviews:@[importBtn, exportBtn, _diagBtn, probeBtn]];
    UIStackView *row2 = [[UIStackView alloc] initWithArrangedSubviews:@[triBtn, kbBtn, audioBtn, libsBtn, logBtn]];
    for (UIStackView *r in @[row1, row2])
    {
        r.axis = UILayoutConstraintAxisHorizontal;
        r.spacing = 8;
        r.alignment = UIStackViewAlignmentCenter;
        r.translatesAutoresizingMaskIntoConstraints = NO;
    }

    UILabel *foot = launcher_label(12, UIFontWeightRegular, UIColor.tertiaryLabelColor);
    foot.text = @"游戏就绪时打开 app 自动进引擎；本界面是诊断与数据入口。\n"
                @"卡片展示上次会话与本次实况；「日志」随时看全文（port17-ui.log）。\n"
                @"JIT 需经 StikDebug 拉起（rw17-jit26.js）；解释器路线装完直接开。";
    foot.textAlignment = NSTextAlignmentCenter;

    // ---- 列布局（s2arm place() 的收窄版：居中窄列，屏窄让安全区，放不下可滚）----
    UIStackView *colStack = [[UIStackView alloc] initWithArrangedSubviews:@[
        title, subtitle,
    ]];
    for (UIVisualEffectView *c in _cards)
        [colStack addArrangedSubview:c];
    [colStack addArrangedSubview:engineBtn];
    [colStack addArrangedSubview:row1];
    [colStack addArrangedSubview:row2];
    [colStack addArrangedSubview:foot];
    colStack.axis = UILayoutConstraintAxisVertical;
    colStack.alignment = UIStackViewAlignmentCenter;
    colStack.spacing = 10;
    [colStack setCustomSpacing:20 afterView:subtitle];
    [colStack setCustomSpacing:18 afterView:_cards.lastObject];
    [colStack setCustomSpacing:14 afterView:engineBtn];
    [colStack setCustomSpacing:16 afterView:row2];
    colStack.translatesAutoresizingMaskIntoConstraints = NO;
    [col addSubview:colStack];
    _colWidthConstraint = [colStack.widthAnchor constraintEqualToConstant:520];
    _colWidthConstraint.priority = UILayoutPriorityDefaultHigh;
    [NSLayoutConstraint activateConstraints:@[
        [_gameView.topAnchor constraintEqualToAnchor:self.view.topAnchor],
        [_gameView.leadingAnchor constraintEqualToAnchor:self.view.leadingAnchor],
        [_gameView.trailingAnchor constraintEqualToAnchor:self.view.trailingAnchor],
        [_gameView.bottomAnchor constraintEqualToAnchor:self.view.bottomAnchor],
        [_keyboard.topAnchor constraintEqualToAnchor:safe.topAnchor],
        [_keyboard.leadingAnchor constraintEqualToAnchor:safe.leadingAnchor],
        [backdrop.topAnchor constraintEqualToAnchor:self.view.topAnchor],
        [backdrop.leadingAnchor constraintEqualToAnchor:self.view.leadingAnchor],
        [backdrop.trailingAnchor constraintEqualToAnchor:self.view.trailingAnchor],
        [backdrop.bottomAnchor constraintEqualToAnchor:self.view.bottomAnchor],
        [backdropBlur.topAnchor constraintEqualToAnchor:self.view.topAnchor],
        [backdropBlur.leadingAnchor constraintEqualToAnchor:self.view.leadingAnchor],
        [backdropBlur.trailingAnchor constraintEqualToAnchor:self.view.trailingAnchor],
        [backdropBlur.bottomAnchor constraintEqualToAnchor:self.view.bottomAnchor],
        [scroll.topAnchor constraintEqualToAnchor:self.view.topAnchor],
        [scroll.leadingAnchor constraintEqualToAnchor:self.view.leadingAnchor],
        [scroll.trailingAnchor constraintEqualToAnchor:self.view.trailingAnchor],
        [scroll.bottomAnchor constraintEqualToAnchor:self.view.bottomAnchor],
        [col.topAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.topAnchor],
        [col.bottomAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.bottomAnchor],
        [col.leadingAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.leadingAnchor],
        [col.trailingAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.trailingAnchor],
        [col.widthAnchor constraintEqualToAnchor:scroll.frameLayoutGuide.widthAnchor],
        [colStack.topAnchor constraintEqualToAnchor:col.topAnchor constant:32],
        [colStack.bottomAnchor constraintEqualToAnchor:col.bottomAnchor constant:-32],
        [colStack.centerXAnchor constraintEqualToAnchor:col.centerXAnchor],
        [colStack.widthAnchor constraintLessThanOrEqualToAnchor:safe.widthAnchor constant:-48],
        [_colWidthConstraint],
        [iconView.centerXAnchor constraintEqualToAnchor:colStack.centerXAnchor],
        [iconView.bottomAnchor constraintEqualToAnchor:title.topAnchor constant:-20],
        [iconView.widthAnchor constraintEqualToConstant:96],
        [iconView.heightAnchor constraintEqualToConstant:96],
        [_badgeDot.centerXAnchor constraintEqualToAnchor:iconView.trailingAnchor constant:-2],
        [_badgeDot.centerYAnchor constraintEqualToAnchor:iconView.bottomAnchor constant:-2],
        [_badgeDot.widthAnchor constraintEqualToConstant:32],
        [_badgeDot.heightAnchor constraintEqualToConstant:32],
        [badgeSym.centerXAnchor constraintEqualToAnchor:_badgeDot.centerXAnchor],
        [badgeSym.centerYAnchor constraintEqualToAnchor:_badgeDot.centerYAnchor],
        [g_text.topAnchor constraintEqualToAnchor:safe.topAnchor constant:8],
        [g_text.leadingAnchor constraintEqualToAnchor:safe.leadingAnchor],
        [g_text.trailingAnchor constraintEqualToAnchor:safe.trailingAnchor],
        [g_text.heightAnchor constraintEqualToAnchor:safe.heightAnchor multiplier:0.4],
    ]];

    // ---- 垫片接线（原 viewDidLoad 原样：日志钩子已由 main() 先装）----
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
    if (prev.length > 32000)
        prev = [prev substringFromIndex:prev.length - 32000];
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

    g_live = self;
    [self refreshCards];
}

- (void)viewDidAppear:(BOOL)animated
{
    [super viewDidAppear:animated];
    boot_log(@"viewDidAppear（启动器）");
    if (!_triangleRan) {
        _triangleRan = YES;
        append_log("==== Port17 M2 宿主 ====");
        append_log("Vulkan 三角形自动启动（启动器背后渲染，拖顶点；「日志」看全文）");
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

- (void)dealloc
{
    if (g_live == self)
        g_live = nil;
}

// ---- 状态卡片刷新：状态仓（上次会话）+ 本次实况 ----

- (void)setDot:(UIView *)dot value:(UILabel *)val
        color:(UIColor *)color text:(NSString *)text
{
    dot.backgroundColor = color ?: UIColor.tertiaryLabelColor;
    val.text = text;
}

- (void)refreshCards
{
    const char *h = NSHomeDirectory().UTF8String;
    char v[448];
    NSFileManager *fm = NSFileManager.defaultManager;

    char gameDir[1024];
    BOOL game = port17_find_game(h, gameDir, sizeof gameDir) == 0;
    BOOL stamped = NO;
    if (game)
        stamped = [fm fileExistsAtPath:
            [NSString stringWithFormat:@"%s/port17-setup.v9.stamp", gameDir]];

    // 1 执行模式（上次会话）：jit_effective + jit_arena
    NSString *jit = port17_status_get(h, "jit_effective", v, sizeof v) ? @(v) : nil;
    NSString *arena = port17_status_get(h, "jit_arena", v, sizeof v) ? @(v) : nil;
    NSString *jitText = jit ?: @"无记录";
    if (arena)
        jitText = [NSString stringWithFormat:@"%@（竞技场 %@）", jitText, arena];
    [self setDot:_cardDots[0] value:_cardValues[0]
           color:(jit == nil) ? nil
                 : [jit hasPrefix:@"JIT"] ? UIColor.systemGreenColor : port17_accent()
            text:jitText];

    // 2 Setup v9：本次实况（戳）+ 上次结果
    NSString *setupLast = port17_status_get(h, "setup_last", v, sizeof v) ? @(v) : nil;
    NSString *setupText = !game ? @"游戏不在"
        : stamped ? @"已改写（v9 戳在）"
        : @"未改写——下次启动先跑 setup";
    if (setupLast)
        setupText = [NSString stringWithFormat:@"%@\n上次：%@", setupText, setupLast];
    [self setDot:_cardDots[1] value:_cardValues[1]
           color:stamped ? UIColor.systemGreenColor : game ? port17_accent() : nil
            text:setupText];

    // 3 Mod（上次会话）
    BOOL hasMods = port17_status_get(h, "mods_last", v, sizeof v);
    [self setDot:_cardDots[2] value:_cardValues[2]
           color:hasMods ? UIColor.systemGreenColor : nil
            text:hasMods ? @(v) : @"无记录"];

    // 4 上次引擎会话：session_last，退而 coreclr_last
    NSString *session = port17_status_get(h, "session_last", v, sizeof v) ? @(v) : nil;
    if (!session && port17_status_get(h, "coreclr_last", v, sizeof v))
        session = [NSString stringWithFormat:@"CoreCLR %@", @(v)];
    UIColor *sessionColor = nil;
    if (session) {
        if ([session hasPrefix:@"信号"])
            sessionColor = UIColor.systemRedColor;
        else if ([session containsString:@"rc 0"] || [session containsString:@"退出码 0"])
            sessionColor = UIColor.systemGreenColor;
        else
            sessionColor = port17_accent();
    }
    [self setDot:_cardDots[3] value:_cardValues[3]
           color:sessionColor text:session ?: @"无记录"];

    // 5 本次实况：游戏就位 + 调试器接管（jit26 的活凭据检测，这一刻的真相）
    int csdbg = port17_jit26_cs_debugged();
    NSString *cs = csdbg == 1 ? @"调试器已接管（可 JIT）"
                 : csdbg == 0 ? @"调试器未接管（解释器路线）"
                 : @"授权检测失败";
    [self setDot:_cardDots[4] value:_cardValues[4]
           color:csdbg == 1 ? UIColor.systemGreenColor : nil
            text:[NSString stringWithFormat:@"%@｜%@", game ? @"游戏就位" : @"游戏不在", cs]];

    // 角标符号按游戏状态换（图标常驻，符号只换语义）
    ((UIImageView *)_badgeDot.subviews.firstObject).image =
        [UIImage systemImageNamed:stamped ? @"checkmark.seal.fill"
                    : game ? @"wrench.and.screwdriver.fill" : @"tray.fill"
             withConfiguration:[UIImageSymbolConfiguration configurationWithPointSize:14 weight:UIImageSymbolWeightBold]];
    _badgeDot.backgroundColor = stamped ? UIColor.systemGreenColor : port17_accent();
}

// ---- 诊断动作（原 HostVC 原样迁移）----

+ (BOOL)diagHoldOn
{
    return [NSFileManager.defaultManager
        fileExistsAtPath:port17_diag_hold_path()];
}

- (void)engineTapped:(id)sender
{
    (void)sender;
    if (_engineRan) {
        append_log("引擎已跑过一次；CoreCLR 单实例，重启 app 才能再跑");
        return;
    }
    if (g_clrSpentBySetup) {
        append_log("本进程的运行时已被 setup 用掉（CoreCLR 单实例）——上划彻底关闭 app 再打开，即进引擎");
        return;
    }
    _engineRan = YES;
    if (_probeRan)
        append_log("注意：探针已用过 CoreCLR——引擎的 coreclr_initialize 大概率 0x80131022，重启 app 直接点「启动引擎」最稳");
    append_log("诊断模式试跑引擎：装载链（TPA/CLR/程序集/引擎 Main）全验证；窗口后端例外——");
    append_log("5014 引擎的 iOS 后端要求主线程并自建 UIApplicationMain，只能由重启后的引擎模式（Documents/game 在即自动）满足");
    g_text.hidden = NO; // 引导期看得见装载日志；「日志」按钮随时收起
    // triangle_stop 最多等 2 秒循环收尾，别冻主线程。
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_DEFAULT, 0), ^{
        triangle_stop();
        run_on_big_thread(engine_thread);
    });
}

- (void)triangleTapped:(id)sender
{
    (void)sender;
    if (!_triangleRan) {
        _triangleRan = YES;
        run_on_big_thread(triangle_main);
    } else {
        append_log("三角形已在跑（一个实例；重启 app 再来一次）");
    }
}

- (void)diagTapped:(UIButton *)sender
{
    NSString *flag = port17_diag_hold_path();
    NSFileManager *fm = NSFileManager.defaultManager;
    if ([fm fileExistsAtPath:flag]) {
        [fm removeItemAtPath:flag error:nil];
        boot_log(@"诊断保持已关——下次启动照常（有游戏即引擎）");
        [sender setTitle:@"下次诊断" forState:UIControlStateNormal];
    } else {
        [fm createFileAtPath:flag
                    contents:[@"on" dataUsingEncoding:NSUTF8StringEncoding]
                  attributes:nil];
        boot_log(@"诊断保持已开——下次启动还进这个界面（引擎先让路；再点一次恢复）");
        [sender setTitle:@"下次进引擎" forState:UIControlStateNormal];
    }
}

- (void)importTapped:(id)sender
{
    (void)sender;
    // 经典初始化器（iOS 8 起就有，SDK 各代可见；新的 forOpeningContentTypes 家族在
    // CI 的 SDK 下两代形态都报 no visible @interface）。Import 模式 = 系统把文件拷进
    // 我们的 tmp 再交 URL——天然的拷贝语义，安全域包装都省。
    UIDocumentPickerViewController *picker =
        [[UIDocumentPickerViewController alloc] initWithDocumentTypes:@[UTTypeZIP.identifier]
                                                                inMode:UIDocumentPickerModeImport];
    picker.delegate = self;
    picker.allowsMultipleSelection = NO;
    [self presentViewController:picker animated:YES completion:nil];
}

- (void)documentPicker:(UIDocumentPickerViewController *)controller
    didPickDocumentsAtURLs:(NSArray<NSURL *> *)urls
{
    (void)controller;
    if (urls.count == 0)
        return;
    NSURL *src = urls[0];
    NSString *dest = [NSHomeDirectory()
        stringByAppendingPathComponent:@"Documents/port17-game.zip"];
    NSFileManager *fm = NSFileManager.defaultManager;
    // iCloud/别的 app 沙盒来的 URL 带 security scope：拷贝期间要持着访问权（本地
    // 文件时这是无害的 no-op）。
    BOOL scoped = [src startAccessingSecurityScopedResource];
    [fm removeItemAtPath:dest error:nil]; // 旧的一份先清（.installed 记录保留——Inbox 收件同规矩）
    NSError *err = nil;
    BOOL copied = [fm copyItemAtURL:src toURL:[NSURL fileURLWithPath:dest] error:&err];
    if (scoped)
        [src stopAccessingSecurityScopedResource];
    if (!copied) {
        boot_log([NSString stringWithFormat:@"导入：拷到 port17-game.zip 失败（%@）",
                  err.localizedDescription]);
        return;
    }
    unsigned long long size = [[fm attributesOfItemAtPath:dest error:nil] fileSize];
    boot_log([NSString stringWithFormat:@"导入：%@ 已就位（%.2f GB），开始安装…",
              urls[0].lastPathComponent, size / 1073741824.0]);
    g_text.hidden = NO; // 看得见安装进度（每 500 项一行）
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_DEFAULT, 0), ^{
        int rc = port17_install_game_zip(NSHomeDirectory().UTF8String);
        dispatch_async(dispatch_get_main_queue(), ^{
            boot_log(rc == 0
                ? @"✅ 游戏就位——彻底关闭 app（上划划掉）再打开：第一次会自动跑 setup"
                   @"（改写游戏文件）并自己退出，再打开即引擎"
                : [NSString stringWithFormat:@"❌ 安装失败（返回 %d，原因在上面日志里）——换一份 zip 再试", rc]);
            [g_live refreshCards];
        });
    });
}

- (void)documentPickerWasCancelled:(UIDocumentPickerViewController *)controller
{
    (void)controller;
    boot_log(@"导入：取消了（没选文件）");
}

- (void)exportTapped:(UIButton *)sender
{
    NSString *home = NSHomeDirectory();
    boot_log(@"导出：打包 Documents/AppData（存档+偏好）为 port17-saves-backup.zip …");
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_DEFAULT, 0), ^{
        char zipPath[1024], srcDir[1024];
        snprintf(zipPath, sizeof zipPath, "%s/Documents/port17-saves-backup.zip",
                 home.UTF8String);
        snprintf(srcDir, sizeof srcDir, "%s/Documents/AppData", home.UTF8String);
        int n = port17_zip_tree(srcDir, zipPath);
        // C 数组进不了 block 的捕获（按值拷数组禁止）——先落到对象再带进去。
        NSString *zipNS = [NSString stringWithUTF8String:zipPath];
        dispatch_async(dispatch_get_main_queue(), ^{
            NSMutableArray<NSURL *> *items = [NSMutableArray array];
            if (n >= 0) {
                boot_log([NSString stringWithFormat:@"导出：AppData 打包完成（%d 个文件）——分享面板里可「存储到文件」或隔空投送", n]);
                [items addObject:[NSURL fileURLWithPath:zipNS]];
            } else {
                boot_log(n == -20
                    ? @"导出：Documents/AppData 不在（还没有存档？）——只分享日志"
                    : [NSString stringWithFormat:@"导出：AppData 打包失败（%d）——只分享日志", n]);
            }
            NSURL *logURL = [NSURL fileURLWithPath:
                [home stringByAppendingPathComponent:@"Documents/port17-ui.log"]];
            if ([NSFileManager.defaultManager fileExistsAtPath:logURL.path])
                [items addObject:logURL];
            if (items.count == 0) {
                boot_log(@"导出：日志和 AppData 都不在——没有可导出的");
                return;
            }
            UIActivityViewController *act =
                [[UIActivityViewController alloc] initWithActivityItems:items
                                                    applicationActivities:nil];
            act.popoverPresentationController.sourceView = sender; // iPad 必须：分享面板要锚点
            act.popoverPresentationController.sourceRect = sender.bounds;
            [self presentViewController:act animated:YES completion:nil];
        });
    });
}

- (void)probeTapped:(id)sender
{
    (void)sender;
    if (_probeRan) {
        // CoreCLR 单实例：进程内二次 coreclr_initialize 会 0x80131022（M1 的已知债）。
        append_log("探针已跑过一次；CoreCLR 单实例，重启 app 才能再跑");
        return;
    }
    _probeRan = YES;
    run_probe_thread();
}

- (void)logTapped:(id)sender
{
    (void)sender;
    g_text.hidden = !g_text.hidden;
    if (!g_text.hidden) {
        NSRange end = { g_text.text.length - 1, 1 };
        [g_text scrollRangeToVisible:end];
    }
}

- (void)keyboardTapped:(id)sender
{
    (void)sender;
    // 直接走垫片的软键盘入口（引擎将来的同一条路）。
    glfwAndroidShowKeyboard(_keyboard.isFirstResponder ? 0 : 1);
}

- (void)audioTapped:(id)sender
{
    (void)sender;
    // waylithaudio 的 CoreAudio 自检（引擎的 wa_* API 面；结束后可再跑）。
    append_log("音频自检启动（waylithaudio / CoreAudio）");
    run_on_big_thread(audio_test);
}

- (void)libsTapped:(id)sender
{
    (void)sender;
    // 全部引擎 dylib 的装载自检（install_name/依赖链在设备上是否成立）。
    run_on_big_thread(libs_check);
}

@end
