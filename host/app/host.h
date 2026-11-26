// M1 探针宿主：拉起 CoreCLR、执行 Hello.dll、扫描可执行内存区域。
// 与安卓仓库 host/host.c 同一思路（dlopen + coreclr_initialize），可执行内存
// 扫描是 iOS 特有的验证（JIT 是否真的分配了 RX 内存）。
#ifndef PORT17_PROBE_HOST_H
#define PORT17_PROBE_HOST_H

#include <stddef.h> // size_t（port17_find_game 的声明用）

// main.m 设置日志回调；host.c 和托管侧的 host_log 都走这里
void host_set_log_sink(void (*sink)(const char *));

// 托管侧经 [DllImport("__Internal")] 调用的入口
void host_log(const char *msg);

// bundle_dir = [[NSBundle mainBundle] bundlePath].UTF8String
// 返回 Hello 的退出码；负数是宿主自身错误
int run_probe(const char *bundle_dir);

// M2 收官：游戏目录 <home>/Documents/game 里的 RimWorld.dll 上官方 CoreCLR 运行。
// bundle_dir 同上；home = NSHomeDirectory。返回引擎退出码；负数是宿主自身错误。
int run_engine(const char *bundle_dir, const char *home);

// 游戏目录解析：Documents/game 直达，或手动解压套了一层（game/<一层>/RimWorld.dll、
// Documents/<其它名>/RimWorld.dll）也认（唯一候选才认）。0 = out 里是找到的目录。
int port17_find_game(const char *home, char *out, size_t cap);

// 硬打点（engine.c）：时间戳 + 一行字直写 <home>/Documents/engine-trace.log（open/append/write/close，
// 绕开一切缓冲与回调链；任意线程）。
void trace_mark(const char *home, const char *what);

// 三角形渲染循环的停止开关（引擎与三角形共用一个 CAMetalLayer 和 GLFW 垫片的单窗口槽，
// 引擎启动前必须让三角形收尾）：置停止位并等循环退出（有超时，从没跑过则立刻返回）。
void triangle_stop(void);

// 设备内游戏改写（setup/ 的 Port17Setup.dll 上 CoreCLR 跑，安卓 SetupService 的 iOS 对应
// 物）：iOS 无 fork，本进程的 CoreCLR 配额被它用掉——跑完由调用方回落诊断界面提示重开。
// 0 = 成功（port17-setup.stamp 已写）；负数 = 失败（日志有因）。
int port17_run_setup(const char *bundle, const char *game);

#endif
