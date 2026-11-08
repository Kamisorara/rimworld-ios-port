// M1 探针的 UIKit 壳：全屏日志视图 + “重新运行”按钮。
// 这是 M2 正式宿主的种子——触摸原始事件、软键盘桥都会长在这个结构上。
#import <UIKit/UIKit.h>
#include <stdio.h>
#include <stdlib.h>
#import "host.h"

static UITextView *g_text = nil;
static dispatch_queue_t g_probe_queue = NULL;

// UI 之外的地面真相：所有阶段打点同步落 Documents/port17-ui.log，
// 黑屏/闪退时经 Files app 也能拿到（Info.plist 开了文件共享）。
static void boot_log(NSString *line)
{
    NSString *path = [NSHomeDirectory()
        stringByAppendingPathComponent:@"Documents/port17-ui.log"];
    NSString *out = [NSString stringWithFormat:@"%@\n", line];
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

static void run_probe_on_bg(void)
{
    append_log("==== Port17 iOS 探针 ====");
    append_log("注意：不经过 StikDebug 开 JIT，JIT 探针应报“疑似解释器”");
    dispatch_async(g_probe_queue, ^{
        // CoreCLR 的内部报错走 stderr（iOS 上默认进系统日志），重定向到文件
        // 再读回来附进屏幕日志——它给出的文件名比 0x80070002 有用得多。
        NSString *errPath = [NSHomeDirectory()
            stringByAppendingPathComponent:@"Documents/coreclr-stderr.log"];
        freopen(errPath.fileSystemRepresentation, "w", stderr);

        const char *bundle =
            [[NSBundle mainBundle].bundlePath UTF8String];
        int rc = run_probe(bundle);

        NSString *err = [NSString stringWithContentsOfFile:errPath
            encoding:NSUTF8StringEncoding error:nil];
        if (err.length > 0) {
            append_log("---- CoreCLR stderr ----");
            append_log(err.UTF8String);
        }

        char buf[64];
        snprintf(buf, sizeof(buf), "run_probe 返回 %d", rc);
        append_log(buf);
    });
}

@interface ProbeVC : UIViewController
@end

@implementation ProbeVC

- (void)viewDidLoad
{
    [super viewDidLoad];
    boot_log(@"viewDidLoad");
    self.view.backgroundColor = UIColor.blackColor;

    g_text = [[UITextView alloc] initWithFrame:CGRectZero];
    g_text.translatesAutoresizingMaskIntoConstraints = NO;
    g_text.editable = NO;
    g_text.selectable = YES;
    g_text.backgroundColor = UIColor.blackColor;
    g_text.textColor = UIColor.greenColor;
    g_text.font = [UIFont fontWithName:@"Menlo" size:12];
    g_text.layoutManager.allowsNonContiguousLayout = NO;
    [self.view addSubview:g_text];

    UIButton *again = [UIButton buttonWithType:UIButtonTypeSystem];
    [again setTitle:@"重新运行" forState:UIControlStateNormal];
    [again setTitleColor:UIColor.systemBlueColor forState:UIControlStateNormal];
    again.translatesAutoresizingMaskIntoConstraints = NO;
    [again addTarget:self action:@selector(runTapped:)
        forControlEvents:UIControlEventTouchUpInside];
    [self.view addSubview:again];

    UILayoutGuide *safe = self.view.safeAreaLayoutGuide;
    [NSLayoutConstraint activateConstraints:@[
        [again.topAnchor constraintEqualToAnchor:safe.topAnchor],
        [again.centerXAnchor constraintEqualToAnchor:safe.centerXAnchor],
        [g_text.topAnchor constraintEqualToAnchor:again.bottomAnchor],
        [g_text.leadingAnchor constraintEqualToAnchor:safe.leadingAnchor],
        [g_text.trailingAnchor constraintEqualToAnchor:safe.trailingAnchor],
        [g_text.bottomAnchor constraintEqualToAnchor:safe.bottomAnchor],
    ]];
}

- (void)viewDidAppear:(BOOL)animated
{
    [super viewDidAppear:animated];
    boot_log(@"viewDidAppear");
    static BOOL ran = NO;
    if (!ran) {
        ran = YES;
        run_probe_on_bg();
    }
}

- (void)runTapped:(id)sender { run_probe_on_bg(); }

@end

@interface AppDelegate : UIResponder <UIApplicationDelegate>
@property (strong, nonatomic) UIWindow *window;
@end

@implementation AppDelegate

- (BOOL)application:(UIApplication *)application
    didFinishLaunchingWithOptions:(NSDictionary *)options
{
    self.window = [[UIWindow alloc]
        initWithFrame:UIScreen.mainScreen.bounds];
    self.window.rootViewController = [[ProbeVC alloc] init];
    [self.window makeKeyAndVisible];
    g_probe_queue = dispatch_queue_create("port17.probe", DISPATCH_QUEUE_SERIAL);
    host_set_log_sink(append_log);
    return YES;
}

@end

int main(int argc, char *argv[])
{
    @autoreleasepool {
        boot_log(@"==== main 进入 ====");
        // Hello 的文件输出也固定到 Documents，黑屏时经 Files app 可读
        setenv("HELLO_OUT", [[NSHomeDirectory()
            stringByAppendingPathComponent:@"Documents/hello.log"]
            fileSystemRepresentation], 1);
        boot_log(@"UIApplicationMain 开始");
        int rc = UIApplicationMain(argc, argv, nil,
                                   NSStringFromClass([AppDelegate class]));
        boot_log(@"UIApplicationMain 返回");
        return rc;
    }
}
