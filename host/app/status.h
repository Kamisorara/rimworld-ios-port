// 启动器状态仓：宿主各阶段把关键事实落 Documents/port17-status.txt（key=value 行），
// 启动器界面读它展示"上次会话"的执行模式 / setup 结果 / mod 数——裁决链的信号不再
// 只埋在日志里。managed 侧的 [port17-status] 行经 stdout 泵 → host_log → append_log
// 的前缀钩子也落到这里（launcher.m），两个方向共用这一个文件。
#ifndef PORT17_STATUS_H
#define PORT17_STATUS_H

#include <stddef.h>

// 覆盖或新增一个键（home = NSHomeDirectory().UTF8String；内部互斥，任意线程可调）。
void port17_status_set(const char *home, const char *key, const char *value);

// 读回一个键的值（buf 收 value；无键返回 0 且 buf 清空）。同名多行时最后一行生效
//（写路径是整文件替换，多行同名只在异常时出现）。
int port17_status_get(const char *home, const char *key, char *buf, size_t cap);

#endif
