// 0.4.48：日志轮转——上一轮整包挪进 Documents/prev-log（用户 2026-11-27 定的规矩）。
// 此前各路日志都是追加：port17-ui.log / engine-trace.log 跨会话一直长，coreclr-stderr.log
// 虽然每次 "w" 重开，但上一轮的 WorkshopLog、record、status 都还留在原地。现在进程一进来
// 就轮转（main 的第一行日志之前），所以本轮所有日志——宿主的和托管的——都从零开始；
// prev-log 里永远只有倒数第二轮，再启动一次就把更旧的丢掉。
#import <Foundation/Foundation.h>
#import <time.h>

static BOOL move_into(NSArray<NSString *> *names, NSString *docs, NSString *prev,
                      NSMutableArray<NSString *> *moved)
{
    NSFileManager *fm = [NSFileManager defaultManager];
    BOOL any = NO;
    for (NSString *name in names)
    {
        NSString *src = [docs stringByAppendingPathComponent:name];
        if (![fm fileExistsAtPath:src])
            continue;
        NSString *dst = [prev stringByAppendingPathComponent:name];
        if ([fm moveItemAtPath:src toPath:dst error:nil])
        {
            [moved addObject:name];
            any = YES;
        }
    }
    return any;
}

int port17_logs_rotate(void)
{
    @autoreleasepool {
        NSFileManager *fm = [NSFileManager defaultManager];
        NSString *docs = [NSHomeDirectory() stringByAppendingPathComponent:@"Documents"];
        NSString *prev = [docs stringByAppendingPathComponent:@"prev-log"];
        // 只留一轮：已有的 prev-log（上上轮）直接丢。
        if ([fm fileExistsAtPath:prev])
            [fm removeItemAtPath:prev error:nil];
        if (![fm createDirectoryAtPath:prev withIntermediateDirectories:YES attributes:nil error:nil])
            return 0;
        NSMutableArray<NSString *> *moved = [NSMutableArray array];
        // 宿主这边的零散日志（文件名与各写入点一致：main.m / engine.c / status.c / launcher.m）。
        move_into(@[@"port17-ui.log", @"hello.log", @"engine-trace.log", @"coreclr-stderr.log",
                    @"port17-status.txt", @"port17-workshop.log"], docs, prev, moved);
        // 托管那边的目录（mobile/Recorder.cs 的 logs/record.txt 与它的 record-1/2.txt 备份）。
        NSString *logsDir = [docs stringByAppendingPathComponent:@"logs"];
        if ([fm fileExistsAtPath:logsDir] &&
            [fm moveItemAtPath:logsDir toPath:[prev stringByAppendingPathComponent:@"logs"] error:nil])
            [moved addObject:@"logs/"];
        // round.txt：标记这一包是哪一轮（什么时候启动的、哪个包），发日志时不用再猜。
        time_t now = time(NULL);
        struct tm tmv;
        gmtime_r(&now, &tmv);
        char when[32];
        strftime(when, sizeof when, "%Y-%m-%d %H:%M:%S UTC", &tmv);
        NSString *stamp = [NSString stringWithFormat:@"上一轮启动于 %s（app 版本 %@）\n", when,
                       [NSBundle mainBundle].infoDictionary[@"CFBundleShortVersionString"] ?: @"?"];
        [stamp writeToFile:[prev stringByAppendingPathComponent:@"round.txt"]
                atomically:YES encoding:NSUTF8StringEncoding error:nil];
        return (int)moved.count;
    }
}
