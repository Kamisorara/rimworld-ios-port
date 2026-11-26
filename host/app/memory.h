// 0.4.44（M5 ① 先量）：jetsam 的账本。安卓层靠 /proc 和 bionic 的 mallopt 量与收内存，
// iOS 两者皆无（0.4.43：MemTrim 每回死在 mallopt，record.txt 的 snap 行没有进程内存）。
// 引擎会话内每 10 s 把 phys_footprint / 剩余额度 / 峰值写进 port17-status.txt（被 jetsam
// 杀掉后峰值也留得住），UIKit 的内存告警记进 port17-ui.log + engine-trace.log 并计数。
// 口径与托管侧 mobile/IosMemory.cs 是同一对函数：proc_pid_rusage(RUSAGE_INFO_V2).ri_phys_footprint
// （系统判杀用的数：匿名脏页 + 压缩页 + IOKit/GPU 映射 + 页表；干净的文件页不算）和
// os_proc_available_memory()（本进程还能拿多少）。
#ifndef PORT17_MEMORY_H
#define PORT17_MEMORY_H

// 引擎会话开始（execute 之前）：登记内存告警观察者、起 10 s 定时器。home = NSHomeDirectory。
void port17_memory_start(const char *home);

// 引擎会话结束（execute 返回之后）：停定时器，engine-trace.log 写一行收官（峰值、告警次数）。
void port17_memory_stop(const char *home);

// 此刻的 phys_footprint（字节；问不到为 0）与剩余额度（字节）。
unsigned long long port17_memory_footprint(void);
long long port17_memory_available(void);

#endif
