// M1 探针宿主：拉起 CoreCLR、执行 Hello.dll、扫描可执行内存区域。
// 与安卓仓库 host/host.c 同一思路（dlopen + coreclr_initialize），可执行内存
// 扫描是 iOS 特有的验证（JIT 是否真的分配了 RX 内存）。
#ifndef PORT17_PROBE_HOST_H
#define PORT17_PROBE_HOST_H

// main.m 设置日志回调；host.c 和托管侧的 host_log 都走这里
void host_set_log_sink(void (*sink)(const char *));

// 托管侧经 [DllImport("__Internal")] 调用的入口
void host_log(const char *msg);

// bundle_dir = [[NSBundle mainBundle] bundlePath].UTF8String
// 返回 Hello 的退出码；负数是宿主自身错误
int run_probe(const char *bundle_dir);

#endif
