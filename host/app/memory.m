#include "memory.h"

#include <dispatch/dispatch.h>
#include <libproc.h>  // proc_pid_rusage
#include <os/proc.h>
#include <stdio.h>
#include <string.h>
#include <sys/resource.h>
#include <unistd.h>

#import <UIKit/UIKit.h>

#include "host.h"      // trace_mark
#include "launcher.h"  // append_log（任意线程：落 Documents/port17-ui.log 并上屏）
#include "status.h"    // port17_status_set（自带锁，整文件原子替换）

// g_peak 只在定时器队列上碰；g_warnings 只在主线程（UIKit 发通知的线程）上碰。
static char g_home[600];
static dispatch_source_t g_timer = nil;
static id g_warningObserver = nil;
static unsigned long long g_peak;
static int g_warnings;

unsigned long long port17_memory_footprint(void)
{
    struct rusage_info_v2 ri;
    memset(&ri, 0, sizeof ri);
    if (proc_pid_rusage(getpid(), RUSAGE_INFO_V2, (rusage_info_t)&ri) != 0)
        return 0;
    return ri.ri_phys_footprint;
}

long long port17_memory_available(void)
{
    return (long long)os_proc_available_memory();
}

static void set_mb(const char *key, unsigned long long bytes)
{
    char v[32];
    snprintf(v, sizeof v, "%llu", bytes >> 20);
    port17_status_set(g_home, key, v);
}

// 当前值每拍都写，峰值只在长高时写：被 jetsam 杀掉后文件里留着的就是峰值。
static void tick(void)
{
    unsigned long long footprint = port17_memory_footprint();
    set_mb("mem_footprint_mb", footprint);
    set_mb("mem_available_mb", (unsigned long long)port17_memory_available());
    if (footprint > g_peak)
    {
        g_peak = footprint;
        set_mb("mem_peak_mb", g_peak);
    }
}

void port17_memory_start(const char *home)
{
    snprintf(g_home, sizeof g_home, "%s", home != NULL ? home : "");
    g_peak = 0;
    g_warnings = 0;
    char v[16];
    snprintf(v, sizeof v, "%d", g_warnings);
    port17_status_set(g_home, "mem_warnings", v);
    // UIKit 自己发的通知，不看 delegate 是谁的（引擎的 UIApplicationMain 带着它自己的 delegate）。
    // queue nil = 在发通知的主线程上同步执行。
    g_warningObserver = [[NSNotificationCenter defaultCenter]
        addObserverForName:UIApplicationDidReceiveMemoryWarningNotification
                    object:nil queue:nil
                usingBlock:^(NSNotification *_) {
                    g_warnings++;
                    unsigned long long footprint = port17_memory_footprint();
                    long long available = port17_memory_available();
                    char line[200];
                    snprintf(line, sizeof line, "⚠️ 内存告警 #%d：footprint %llu MB，剩余额度 %lld MB",
                             g_warnings, footprint >> 20, available >> 20);
                    append_log(line);
                    if (g_home[0] != '\0')
                    {
                        char mark[200];
                        snprintf(mark, sizeof mark, "memory warning #%d footprint=%llu available=%lld",
                                 g_warnings, footprint >> 20, available >> 20);
                        trace_mark(g_home, mark);
                        char v[16];
                        snprintf(v, sizeof v, "%d", g_warnings);
                        port17_status_set(g_home, "mem_warnings", v);
                    }
                }];
    g_timer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0,
                                     dispatch_get_global_queue(QOS_CLASS_UTILITY, 0));
    dispatch_source_set_timer(g_timer, DISPATCH_TIME_NOW, 10 * NSEC_PER_SEC, NSEC_PER_SEC);
    dispatch_source_set_event_handler(g_timer, ^{ tick(); });
    dispatch_resume(g_timer);
}

void port17_memory_stop(const char *home)
{
    if (g_timer != nil)
    {
        dispatch_source_cancel(g_timer);
        g_timer = nil;
    }
    if (g_warningObserver != nil)
    {
        [[NSNotificationCenter defaultCenter] removeObserver:g_warningObserver];
        g_warningObserver = nil;
    }
    tick();
    char mark[160];
    snprintf(mark, sizeof mark, "memory: peak %llu MB, %d warnings", g_peak >> 20, g_warnings);
    trace_mark(home, mark);
}
