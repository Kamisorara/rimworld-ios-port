// The M2 game host: lifecycle and boot decisions live here — the engine mode (the game's
// iOS backend takes the main thread and builds its own UIApplicationMain), the in-app setup
// run (iOS has no fork: it spends the process's one CoreCLR, then exits back), the zip
// auto-install, the crash rescue. All the UI — the s2arm-style launcher with its status
// cards (jit mode, setup, mods) — and the log plumbing live in launcher.m; the sinks are
// installed from main() before UIApplicationMain, so the engine mode still gets its log
// lines to disk (Documents/port17-ui.log survives crashes; Files app can take them out).
#import <UIKit/UIKit.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#import "host.h"
#import "unzip.h"
#import "status.h"
#import "launcher.h"
#import "jit26.h"

@interface AppDelegate : UIResponder <UIApplicationDelegate>
@property (strong, nonatomic) UIWindow *window;
@end

// 场景生命周期：Info.plist 开了 UIApplicationSceneManifest（引擎的 iOS 后端要它——
// WaylithApplicationDelegate 经 configurationForConnectingSceneSession 给自己的 scene
// delegate），我们的启动器也跟着搬到 scene 里，否则清单在而窗口不走 scene = 黑屏。
@interface SceneDelegate : UIResponder <UIWindowSceneDelegate>
@property (strong, nonatomic) UIWindow *window;
@end

@implementation SceneDelegate

// 引擎会话的后台任务断言。iOS 在 app 进后台（熄屏/上划/切走）时拒绝执行 Metal 命令缓冲
// （kIOGPUCommandBufferCallbackErrorBackgroundExecutionNotPermitted），而 MoltenVK 对这个
// 错误无条件判 VK_ERROR_DEVICE_LOST——v1.4.2 与上游 main 同款：handleMTLCommandBufferError
// 的 NotPermitted 分支把 markDeviceLoss 强制 true，无视 MVK_CONFIG_RESUME_LOST_DEVICE（引擎
// 自己配的救不了）——m2-engine-14 的熄屏闪退链：在途命令缓冲被杀 → 设备丢失 → 引擎退出
// 路径 RenderThread.Stop 再撞 → UIKit 循环未处理异常 FailFast。持有 UIKit 后台断言后 iOS
// 给 ~30 秒"完成在途工作"窗口（Metal 后台的标准姿势）；引擎自己的 HoldBackgroundTask 只在
// 抑制节流时才拿，装载期是空的。过期干净归还，回前台重新持有（下一次熄屏又有窗口）。
static UIBackgroundTaskIdentifier g_engineBgTask = (UIBackgroundTaskIdentifier)~(NSUInteger)0; // = UIBackgroundTaskInvalid（extern const 当不了编译期常量）
static id g_bgTaskObserver = nil; // 观察者令牌要持有（不持有可能被提前释放，重挂静默失效）
static id g_activeObserver = nil, g_inactiveObserver = nil;

// waylithaudio 的帧停摆看门狗要区分前后台（后台显示链接本来就停，不算停摆）——宿主
// 在这里通报状态。宿主链接了 -lwaylithaudio，符号直接可用。
extern void wa_port17_set_active(int active);

// 装载期观测：主 runloop 心跳。引擎装载期 stdout 安静（卡 Initializing 排查），这行
// 是主线程死活的信号——主 runloop 卡死即停跳（主队列的 dispatch_after 不再伺候）；
// 配合 waylithaudio 的帧计数心跳区分三种形态：主 runloop 停+帧停=主线程忙/死锁，
// 主 runloop 活+帧停=引擎循环卡，两者都活=纯慢，等就是了。
static void engine_main_heartbeat(void)
{
    append_log("💓 主 runloop 存活（引擎装载期 stdout 安静属正常）");
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(15 * NSEC_PER_SEC)),
                   dispatch_get_main_queue(), ^{ engine_main_heartbeat(); });
}
static void engine_hold_background_task(void)
{
    if (g_engineBgTask != UIBackgroundTaskInvalid)
        return;
    g_engineBgTask = [[UIApplication sharedApplication]
        beginBackgroundTaskWithExpirationHandler:^{
            if (g_engineBgTask != UIBackgroundTaskInvalid)
            {
                [[UIApplication sharedApplication] endBackgroundTask:g_engineBgTask];
                g_engineBgTask = UIBackgroundTaskInvalid;
            }
        }];
    append_log("🛡️ 引擎后台断言已持有（熄屏/切走时在途 GPU 工作有约 30 秒完成窗口）");
}

// 启动器「启动游戏」按钮（诊断模式的大线程引擎尝试）的熄屏保护入口：正式引擎路（自动
// 进引擎）由上面的 scene 转交分支持有断言；这条入口保证诊断尝试从点击起也有同样的
// DEVICE_LOST 保护（engineTapped 在主线程上调，UIApplication API 要求主线程）。
void port17_launcher_hold_bg_task(void) { engine_hold_background_task(); }

- (void)scene:(UIScene *)scene willConnectToSession:(UISceneSession *)session options:(UISceneConnectionOptions *)options
{
    // 引擎的 UIKit 会话把 scene 交到了我们手里（七跑铁证：execute 活着、引擎的
    // UIApplicationMain 在等它的 WaylithSceneDelegate 收到 willConnect，UIKit 却调了
    // 我们的——引擎因此永远等不到 StartApplication，黑屏"无事发生"）。这里转交：
    // WaylithSceneDelegate 类在引擎跑起来时才注册（运行时 ClassBuilder），引擎没跑
    // 时不存在——正好也是"进引擎还是进启动器"的判据。
    Class waylith = NSClassFromString(@"WaylithSceneDelegate");
    if (waylith != NULL)
    {
        append_log("⚠️ scene 转交引擎（WaylithSceneDelegate 在，实例化并转发 willConnect）");
        // 先持断言再转交（此时 UIApplicationMain 已在跑，sharedApplication 可用）
        engine_hold_background_task();
        engine_main_heartbeat();
        g_bgTaskObserver = [[NSNotificationCenter defaultCenter]
            addObserverForName:UIApplicationDidBecomeActiveNotification
                        object:nil queue:[NSOperationQueue mainQueue]
                    usingBlock:^(NSNotification *_) {
                        wa_port17_set_active(1);
                        engine_hold_background_task();
                    }];
        g_inactiveObserver = [[NSNotificationCenter defaultCenter]
            addObserverForName:UIApplicationDidEnterBackgroundNotification
                        object:nil queue:[NSOperationQueue mainQueue]
                    usingBlock:^(NSNotification *_) { wa_port17_set_active(0); }];
        id engineDelegate = [[waylith alloc] init];
        scene.delegate = engineDelegate;
        if ([engineDelegate respondsToSelector:@selector(scene:willConnectToSession:options:)])
            [engineDelegate scene:scene willConnectToSession:session options:options];
        return;
    }
    append_log("诊断 scene 连接（WaylithSceneDelegate 不在 = 引擎没跑，启动器）");
    UIWindowScene *windowScene = (UIWindowScene *)scene;
    self.window = [[UIWindow alloc] initWithWindowScene:windowScene];
    self.window.rootViewController = [[LauncherVC alloc] init];
    [self.window makeKeyAndVisible];
}

@end

@implementation AppDelegate

- (BOOL)application:(UIApplication *)application
    didFinishLaunchingWithOptions:(NSDictionary *)options
{
    (void)application;
    (void)options;
    return YES; // 日志 sink 已在 main() 装好（引擎模式要更早）
}

- (UISceneConfiguration *)application:(UIApplication *)application
    configurationForConnectingSceneSession:(UISceneSession *)connectingSceneSession
    options:(UISceneConnectionOptions *)options
{
    (void)application;
    (void)connectingSceneSession;
    (void)options;
    UISceneConfiguration *config = [UISceneConfiguration configurationWithName:@"diagnostics"
                                                             sessionRole:connectingSceneSession.role];
    config.delegateClass = SceneDelegate.class;
    return config;
}

@end

// 崩溃信号：落一行打点 + 把 coreclr-stderr.log 尾部抢救进持久化的 port17-ui.log（M1 的 best-effort），
// 然后交还系统默认处理写 .ips 报告。没有任何信号行而进程消失 = SIGKILL（jetsam/看门狗）。
// session_last 也记一笔：启动器「上次引擎会话」卡片直接给出崩溃信号（status 仓与
// port17-ui.log 同一份磁盘，信号上下文里写文件与既有 boot_log 同一风险面）。
static void crash_handler(int sig)
{
    char buf[96];
    snprintf(buf, sizeof(buf),
             "收到信号 %d（SIGSEGV=11, SIGBUS=10, SIGABRT=6, SIGTRAP=5, SIGILL=4），转系统写崩溃报告",
             sig);
    boot_log([NSString stringWithUTF8String:buf]);
    {
        char s[32];
        snprintf(s, sizeof(s), "信号 %d", sig);
        port17_status_set(NSHomeDirectory().UTF8String, "session_last", s);
    }
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

static char *trim_perf_value(char *value)
{
    while (*value == ' ' || *value == '\t') value++;
    char *end = value + strlen(value);
    while (end > value && (end[-1] == ' ' || end[-1] == '\t' ||
                           end[-1] == '\r' || end[-1] == '\n'))
        end--;
    *end = 0;
    return value;
}

// Perf controls live beside the game and are read before MoltenVK can be loaded. Since 0.4.29
// the engine's triple-buffer default lives in the interposer; 0 disables it and 2 forces the
// double-buffer comparison.
static void apply_perf_config(void)
{
    NSString *path = [NSHomeDirectory()
        stringByAppendingPathComponent:@"Documents/game/port17-perf.txt"];
    FILE *file = fopen(path.fileSystemRepresentation, "r");
    if (!file)
        return;

    char line[128];
    while (fgets(line, sizeof line, file))
    {
        char *cursor = line;
        while (*cursor == ' ' || *cursor == '\t') cursor++;
        if (*cursor == 0 || *cursor == '#' || *cursor == ';')
            continue;
        char *separator = strchr(cursor, '=');
        if (!separator)
            continue;
        *separator = 0;
        char *key = trim_perf_value(cursor);
        char *value = trim_perf_value(separator + 1);
        if (!strcmp(key, "swapchainImages") || !strcmp(key, "triangleImages"))
        {
            int wanted = atoi(value);
            if (wanted >= 0 && wanted <= 8)
            {
                setenv(!strcmp(key, "swapchainImages") ? "PORT17_SWAPCHAIN_IMAGES"
                                                       : "PORT17_TRIANGLE_IMAGES",
                       value, 1);
                boot_log([NSString stringWithFormat:@"perf config: %s=%s", key, value]);
            }
        }
        else if (!strcmp(key, "mvkPerformanceTracking") ||
                 !strcmp(key, "mvkSynchronousQueueSubmits") ||
                 !strcmp(key, "mvkPrefillMetalCommandBuffers"))
        {
            const char *env = "MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS";
            if (!strcmp(key, "mvkPerformanceTracking"))
                env = "MVK_CONFIG_PERFORMANCE_TRACKING";
            else if (!strcmp(key, "mvkSynchronousQueueSubmits"))
                env = "MVK_CONFIG_SYNCHRONOUS_QUEUE_SUBMITS";
            setenv(env, value, 1);
            boot_log([NSString stringWithFormat:@"perf config: %s=%s", key, value]);
        }
    }
    fclose(file);
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
        // 自证身份 + 安装后 plist 完整性：运行时读到的就是 SideStore 重写后的 plist
        // （ResignAppOperation 无条件重命名 App.app/注入 URL 类型）。如果
        // UIFileSharingEnabled/CFBundleIcons 不在下面的键列表里 = 重写丢键实锤。
        boot_log([NSString stringWithFormat:@"安装身份：id=%@ 版本=%@ bundle=%@",
            [NSBundle mainBundle].bundleIdentifier,
            [NSBundle mainBundle].infoDictionary[@"CFBundleShortVersionString"] ?: @"?",
            [NSBundle mainBundle].bundlePath.lastPathComponent]);
        NSArray<NSString *> *plistKeys = [[NSBundle mainBundle].infoDictionary.allKeys
            sortedArrayUsingSelector:@selector(compare:)];
        boot_log([NSString stringWithFormat:@"安装后 plist 键（%lu 个）：%@",
            (unsigned long)plistKeys.count, [plistKeys componentsJoinedByString:@", "]]);
        signal(SIGSEGV, crash_handler);
        signal(SIGBUS, crash_handler);
        signal(SIGABRT, crash_handler);
        signal(SIGTRAP, crash_handler);
        signal(SIGILL, crash_handler);
        // 日志 sink 一开始就装：引擎模式下宿主 UIKit 还没起来，host_log 的行经
        // append_log 落 port17-ui.log（启动器的屏幕部分排队等 UIApplicationMain 后补上屏）。
        port17_ui_install_sinks();
        apply_perf_config();
        setenv("HELLO_OUT", [[NSHomeDirectory()
            stringByAppendingPathComponent:@"Documents/hello.log"]
            fileSystemRepresentation], 1);
        // MoltenVK 稳定性旋钮（MHGURecomp-iOS 的实战教训，M2/iOS18 起 residency sets 不稳）：
        // 必须在 MoltenVK 装载前设置——它读环境变量的时机是实例创建。用户设备 M4/iOS27 属于
        // 同一风险面，关掉只影响性能优化路径，不影响功能。
        setenv("MVK_CONFIG_RESIDENCY_SETS", "0", 0);
        setenv("MVK_CONFIG_USE_MTLRESIDENCYSETS", "0", 0);

        // “打开方式 → Port17”的备用导入通道（Files 里看不到 Port17 文件夹时的正路）：
        // 在文件 app 里点 port17 开头的 zip → 打开方式 → Port17，iOS 把它拷进
        // Documents/Inbox 再拉起 app——冷启动时这里收件挪成 port17-game.zip，下面的
        // 自动安装照常接手。只认 port17 开头的 zip（用户点开别的 zip 不误吞）；
        // 暖启动（app 已在跑）时拷贝要到下次冷启动才被收——“没反应就彻底关掉重开”。
        {
            NSString *inbox = [docDir stringByAppendingPathComponent:@"Inbox"];
            for (NSString *f in [fm contentsOfDirectoryAtPath:inbox error:nil])
            {
                if (![f.pathExtension.lowercaseString isEqualToString:@"zip"] ||
                    ![f.lowercaseString hasPrefix:@"port17"])
                    continue;
                NSString *src = [inbox stringByAppendingPathComponent:f];
                NSString *dst = [docDir stringByAppendingPathComponent:@"port17-game.zip"];
                [fm removeItemAtPath:dst error:nil]; // 旧的一份先清（.installed 记录保留）
                NSError *mvErr = nil;
                if ([fm moveItemAtPath:src toPath:dst error:&mvErr])
                    boot_log([NSString stringWithFormat:@"Inbox 收件：%@ → port17-game.zip（打开方式导入，自动安装接手）", f]);
                else
                    boot_log([NSString stringWithFormat:@"Inbox 收件失败（%@）：%@", f, mvErr.localizedDescription]);
            }
        }

        // zip 自动安装：Documents/port17-game.zip 在就解压成 Documents/game（PC 上
        // scripts/pack-game-zip.py 的产物；换新版游戏也是丢一个新 zip 的事——旧 game 自动
        // 挪 game.previous）。主线程上活儿放线程里干，runloop 切片泵着等（launch watchdog
        // 数的是 app 不伺候 runloop 的秒数）。
        int installed = port17_install_game_zip(NSHomeDirectory().UTF8String);
        if (installed < 0)
            boot_log(@"zip 安装失败——见日志；继续按有无 game 判定");

        // 引擎模式：Documents/game 有游戏就走（port17_find_game 也认手动解压套了一层/改名
        // 的目录）。游戏目录没有改写戳（Steam 原件直接拷的场景）→ 先跑设备内 setup（安卓
        // SetupService 的体验：app 自己改写游戏文件，玩家无感 PC）；setup 用掉本进程的
        // CoreCLR（iOS 无 fork），完成或失败都回落启动器——重开即引擎。
        // 诊断保持开着则整个让路（导入/导出/JIT 实验的反复回合不用跟游戏抢启动）。
        BOOL diagHold = [fm fileExistsAtPath:port17_diag_hold_path()];
        if (diagHold)
            boot_log(@"诊断保持开着（port17-diag.on）——引擎与 setup 让路，直接启动器");
        char gameDir[1024];
        if (!diagHold && port17_find_game(NSHomeDirectory().UTF8String, gameDir, sizeof gameDir) == 0) {
            NSString *gameNS = [NSString stringWithUTF8String:gameDir];
            // 只认当前版本的 setup 戳——旧的 os-detect+platform 组合旁路曾把版本轮换架空
            // （v6 补丁在设备上从未执行：用户游戏目录里有这对老戳，setupDone 恒真）。
            // PC 打包流程的旧戳副本：setup 重跑一遍全幂等，顺手补写当前版戳，无害。
            NSString *stamp = [gameNS stringByAppendingPathComponent:@"port17-setup.v10.stamp"];
            BOOL setupDone = [fm fileExistsAtPath:stamp];
            // 竞技场必须先于任何 coreclr_initialize 到位：混合运行时在 iOS 27 上没有
            // 竞技场就没有可执行内存——解释器会话同样要执行生成的桩/预代码（.ips 裁决：
            // CreateAppDomainWithManager 里 blr 进 rw- 页，CODESIGNING Invalid Page；非 JIT
            // 从 0.3.3 起就没真正通过）。0.4.0 把这一步漏在 setup 之后，JIT 会话也死在
            // setup 的初始化里（黑屏）。0.4.1：启动路径先拿竞技场，再决定 setup 还是引擎。
            int csdbg = port17_jit26_cs_debugged();
            int arena = csdbg == 1 ? port17_jit26_acquire() : 0;
            port17_status_set([NSHomeDirectory() UTF8String], "jit_arena",
                              csdbg == 1 ? (arena == 1 ? "granted" : "failed")
                                         : "skipped（无调试器）");
            if (arena != 1) {
                boot_log(@"没有 JIT 竞技场——本版运行时在 iOS 27 上只能走 JIT 会话（解释器会话"
                         @"同样需要可执行内存，0.3.3 起非 JIT 从未通过）。请经 StikDebug + "
                         @"rw17-jit26.js 拉起本 app；普通启动停在启动器，不硬闯引擎。");
                append_log("⚠️ 本版需经 StikDebug + rw17-jit26.js 拉起（普通启动无竞技场，引擎不启动）");
            }
            else if (!setupDone) {
                boot_log(@"游戏文件是原件（没有改写戳）——先跑设备内 setup（安卓同款体验），完成后请重开 app");
                int rc = port17_run_setup([[NSBundle mainBundle].bundlePath UTF8String], gameDir);
                boot_log([NSString stringWithFormat:@"setup 返回 %d——%@",
                    rc,
                    rc == 0 ? @"改写完成，请彻底关闭 app（上划划掉）再打开，即进引擎"
                           : @"改写没完成（原因在上面日志），可重试或换打包好的 zip"]);
                if (rc == 0) {
                    port17_launcher_note_clr_spent();
                    append_log("✅ 游戏文件改写完成——app 几秒后自动关闭，重新打开即进引擎");
                    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(4 * NSEC_PER_SEC)),
                                   dispatch_get_main_queue(), ^{
                        boot_log(@"setup 完成后自动关闭（重新打开即引擎模式）");
                        exit(0);
                    });
                }
                else
                    append_log("⚠️ setup 没有完成——按日志里的原因处理（重开 app 会再试一次）");
            } else {
                // 自动进引擎（回退裁决 2026-11-23）：IOS 后端 + 主线程是唯一有真机工时的
                // 引擎路（0.3.6 出图/可玩/JIT 全在这条链上，M4 触控六件套本就为它设计）。
                // v2（AndroidPlatform + 大线程 + 桥）六个环节零设备验证——回退成开关：
                // engine.c 不再置 WAYLITH_BACKEND、IosHost 桥不点亮（AndroidBackend.Open
                // 的 IsInitialized 门过不去，IOS 后端自然赢），v10 补丁在这条路上休眠无害。
                // v2 待 IOS 路还清出图/触控/JIT 欠账后再单独开闸（engine.c 置 env + launcher
                // 点「启动游戏」喂层，代码全在）。
                boot_log(@"引擎模式：游戏目录在且已改写，主线程直接跑引擎（宿主 UIKit 退位）");
                const char *bundle = [[NSBundle mainBundle].bundlePath UTF8String];
                const char *home = [NSHomeDirectory() UTF8String];
                int rc = run_engine(bundle, home);
                char s[64];
                snprintf(s, sizeof(s), "引擎 rc %d", rc);
                port17_status_set(home, "session_last", s);
                boot_log([NSString stringWithFormat:@"引擎模式结束（run_engine 返回 %d）——落回启动器", rc]);
            }
        }

        boot_log(@"UIApplicationMain 开始");
        int rc = UIApplicationMain(argc, argv, nil,
                                   NSStringFromClass([AppDelegate class]));
        boot_log(@"UIApplicationMain 返回");
        return rc;
    }
}
