// See jit26.h. The playport-shaped protocol, verbatim from the M1 probe host
// (probe/app/host.c, the eb1ab5f "runtime brks, script serves, detach before
// anything executes" design) - the brk trampolines, the alias, the probe and
// the env handoff kept as-is: that code was reviewed against the STS2
// disassembly and the M1 device logs, and stability of the acquisition shape
// is what keeps the runtime patch rebuild-free.
#include "host.h"
#include "jit26.h"

#include <TargetConditionals.h>
#include <errno.h>
#include <libkern/OSCacheControl.h>
#include <mach/mach.h>
#include <setjmp.h>
#include <signal.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/sysctl.h>
#include <unistd.h>

#define RW17_CAP 0x8000000ull // 128 MiB，与 rw17-jit26.js 的 _M 尺寸一致（x1 携带）
// 顶部 4 MiB 留给 MonoMod（Port17ArenaAllocator，实际用量几十 KB）：分离后进程不能再申请新的可执行内存
// （mmap(MAP_JIT|RWX) 被内核拒绝，m4-touch-15 裁决 MemoryAllocator.TryAllocate 断言），
// Harmony 的 detour 桩/蹦床只能从这份祝福过的竞技场里分。运行时拿到的是缩小后的 SIZE，
// 其分配是 [rxBase, rxBase+capacity) 内的指针算术，不会越进 MonoMod 的切片。
#define RW17_MM_CAP 0x400000ull

#ifndef P_TRACED
#define P_TRACED 0x00000800
#endif
#ifndef CS_OPS_STATUS
#define CS_OPS_STATUS 0
#endif
#ifndef CS_DEBUGGED
#define CS_DEBUGGED 0x10000000 // 调试器启动/附着时置位；分离后仍保留（非活态凭据）
#endif
// csops 无公开头（精简 SDK 连 sys/codesign.h 都没有）；syscall 本体在 libSystem
extern int csops(pid_t pid, unsigned int ops, void *useraddr, size_t usersize);

static bool jit26_debugger_attached(void)
{
    pid_t pid = getpid();
    uint32_t flags = 0;
    if (csops(pid, CS_OPS_STATUS, &flags, sizeof(flags)) != 0 || !(flags & CS_DEBUGGED))
        return false;
    int mib[4] = {CTL_KERN, KERN_PROC, KERN_PROC_PID, pid};
    struct kinfo_proc info;
    size_t size = sizeof(info);
    memset(&info, 0, sizeof(info));
    if (sysctl(mib, 4, &info, &size, NULL, 0) != 0 || size != sizeof(info))
        return false;
    return (info.kp_proc.p_flag & P_TRACED) != 0;
}

int port17_jit26_cs_debugged(void)
{
    uint32_t flags = 0;
    if (csops(getpid(), CS_OPS_STATUS, &flags, sizeof flags) != 0)
        return -1;
    return (flags & CS_DEBUGGED) ? 1 : 0;
}

// 双参蹦床是协议关键：单参会把容量错放进 x0，脚本的 x0==NULL 校验当场抛错
//（真机二测的教训——"arena granted at 0x8000000" 就是残留在 x0 里的容量，
// 拿一个从未分配的地址去做 vm_remap，别名自然失败）。
__attribute__((noinline, optnone, naked)) static void *jit26_prepare_region(void *addr, size_t len)
{
    __asm__("mov x16, #1\n"
            "brk #0xf00d\n"
            "ret\n");
}

__attribute__((noinline, optnone, naked)) static void jit26_detach(void)
{
    __asm__("mov x16, #0\n"
            "brk #0xf00d\n"
            "ret\n");
}

static bool jit26_wait_debugger(int wait_s)
{
    char buf[128];
    host_log("[jit26] 等待活调试器——去 StikDebug 运行 rw17-jit26.js（脚本随包：启动器「JIT 脚本」或 文件 app → Port17 → stikdebug）");
    for (int waited = 0; waited <= wait_s; waited++) {
        if (jit26_debugger_attached())
            return true;
        if (waited == wait_s)
            break;
        sleep(1);
        if (waited > 0 && waited % 15 == 0) {
            snprintf(buf, sizeof(buf), "[jit26] 仍在等待活调试器（%d/%d 秒）…", waited, wait_s);
            host_log(buf);
        }
    }
    snprintf(buf, sizeof(buf), "[jit26] %d 秒内没有活调试器——无人接的 brk 会杀进程，本次放弃", wait_s);
    host_log(buf);
    return false;
}

static uint8_t *jit26_map_alias_rw(void *source, size_t size)
{
    char buf[128];
    vm_address_t alias = 0;
    vm_prot_t cur = VM_PROT_NONE, maxp = VM_PROT_NONE;
    kern_return_t kr = vm_remap(mach_task_self(), &alias, size, 0, VM_FLAGS_ANYWHERE,
                                mach_task_self(), (vm_address_t)source, FALSE,
                                &cur, &maxp, VM_INHERIT_NONE);
    if (kr != KERN_SUCCESS) {
        snprintf(buf, sizeof(buf), "[jit26] vm_remap 失败：kr=%d (0x%x)", (int)kr, (unsigned)kr);
        host_log(buf);
        return NULL;
    }
    snprintf(buf, sizeof(buf), "[jit26] 别名 %p（remap 给出 cur=%x max=%x）",
             (void *)alias, (unsigned)cur, (unsigned)maxp);
    host_log(buf);
    if (mprotect((void *)alias, size, PROT_READ | PROT_WRITE) == 0)
        return (uint8_t *)alias;
    snprintf(buf, sizeof(buf), "[jit26] mprotect RW 被拒（errno=%d），改走 vm_protect（Mach 路径）", errno);
    host_log(buf);
    kr = vm_protect(mach_task_self(), alias, size, FALSE, VM_PROT_READ | VM_PROT_WRITE);
    if (kr == KERN_SUCCESS)
        return (uint8_t *)alias;
    snprintf(buf, sizeof(buf), "[jit26] vm_protect 也被拒：kr=%d (0x%x)", (int)kr, (unsigned)kr);
    host_log(buf);
    vm_deallocate(mach_task_self(), alias, size);
    return NULL;
}

static sigjmp_buf g_jit26_probe_jmp;
static volatile sig_atomic_t g_jit26_probe_faulted;

static void jit26_probe_fault_handler(int sig, siginfo_t *info, void *ctx)
{
    (void)ctx;
    char buf[96];
    g_jit26_probe_faulted = 1;
    snprintf(buf, sizeof(buf), "[jit26] 探针触发 fault（sig=%d，地址 %p）——按干净失败处理",
             sig, info ? info->si_addr : NULL);
    host_log(buf);
    siglongjmp(g_jit26_probe_jmp, 1);
}

// 写 RW 视图、经 RX 视图执行两段自写机器码（42/43）。只在分离之后跑：
// 带调试器时 fault 先到 debugserver，本 handler 永远没机会执行。
static bool jit26_probe_execute(uint8_t *rxView, uint8_t *rwView)
{
    struct sigaction fa, oldBus, oldSegv, oldIll;
    memset(&fa, 0, sizeof(fa));
    fa.sa_sigaction = jit26_probe_fault_handler;
    fa.sa_flags = SA_SIGINFO;
    sigemptyset(&fa.sa_mask);
    if (sigaction(SIGBUS, &fa, &oldBus) != 0 || sigaction(SIGSEGV, &fa, &oldSegv) != 0 ||
        sigaction(SIGILL, &fa, &oldIll) != 0)
        return false;
    const uint32_t g42[2] = {0x52800540u, 0xD65F03C0u}; // movz w0,#42 ; ret
    const uint32_t g43 = 0x52800560u;                   // movz w0,#43
    bool ok = false;
    g_jit26_probe_faulted = 0;
    if (sigsetjmp(g_jit26_probe_jmp, 1) == 0) {
        memcpy(rwView, g42, sizeof(g42));
        sys_icache_invalidate(rwView, sizeof(g42));
        sys_icache_invalidate(rxView, sizeof(g42));
        int32_t (*fn)(void) = (int32_t (*)(void))rxView;
        int32_t r1 = fn();
        char buf[64];
        snprintf(buf, sizeof(buf), "[jit26] 探针 #1 → %d（期望 42）", r1);
        host_log(buf);
        if (r1 == 42) {
            memcpy(rwView, &g43, sizeof(g43));
            sys_icache_invalidate(rwView, sizeof(g43));
            sys_icache_invalidate(rxView, sizeof(g43));
            int32_t r2 = fn();
            snprintf(buf, sizeof(buf), "[jit26] 探针 #2 → %d（期望 43）", r2);
            host_log(buf);
            ok = (r2 == 43);
        }
    }
    sigaction(SIGBUS, &oldBus, NULL);
    sigaction(SIGSEGV, &oldSegv, NULL);
    sigaction(SIGILL, &oldIll, NULL);
    return !g_jit26_probe_faulted && ok;
}

int port17_jit26_acquire(void)
{
    char buf[320];
    if (!jit26_wait_debugger(120))
        return 0;
    // 发 brk 前紧贴着再查一次（playport 纪律：会话可在等待与 brk 之间消失）
    if (!jit26_debugger_attached()) {
        host_log("[jit26] 调试器在发 brk 前消失，放弃（无人接的 brk = SIGTRAP）");
        return 0;
    }
    host_log("[jit26] prepare brk 发出（128 MiB；本进程停在 brk 上，脚本逐页扫写约 1–2 分钟）");
    void *arena = jit26_prepare_region(NULL, (size_t)RW17_CAP);
    if (arena == NULL) {
        host_log("[jit26] prepare 返回 NULL，无可用 JIT 内存");
        return 0;
    }
    snprintf(buf, sizeof(buf), "[jit26] 竞技场拿到 %p", arena);
    host_log(buf);
    {
        vm_address_t q = (vm_address_t)arena;
        vm_size_t qSize = 0;
        vm_region_basic_info_data_64_t qi;
        mach_msg_type_number_t qc = VM_REGION_BASIC_INFO_COUNT_64;
        mach_port_t qo = MACH_PORT_NULL;
        if (vm_region_64(mach_task_self(), &q, &qSize, VM_REGION_BASIC_INFO_64,
                         (vm_region_info_t)&qi, &qc, &qo) == KERN_SUCCESS) {
            if (qo != MACH_PORT_NULL)
                mach_port_deallocate(mach_task_self(), qo);
            snprintf(buf, sizeof(buf), "[jit26] 内核视角：start=%p size=%llu cur=%x max=%x",
                     (void *)q, (unsigned long long)qSize,
                     (unsigned)qi.protection, (unsigned)qi.max_protection);
            host_log(buf);
        }
    }
    uint8_t *rwAlias = jit26_map_alias_rw(arena, (size_t)RW17_CAP);
    if (rwAlias == NULL) {
        host_log("[jit26] RW 别名失败——无可用 JIT 内存");
        return 0;
    }
    host_log("[jit26] RW 别名就绪，立即请求分离");
    if (jit26_debugger_attached()) {
        jit26_detach();
        host_log("[jit26] 调试器已分离——祝福让页面保持可执行，此后 fault 归本进程");
    } else {
        host_log("[jit26] 调试器已先行消失，跳过分隔 brk，直接探针");
    }
    if (!jit26_probe_execute((uint8_t *)arena, rwAlias)) {
        host_log("[jit26] 执行探针失败——无可用 JIT 内存");
        munmap(rwAlias, (size_t)RW17_CAP);
        return 0;
    }
    // （深页抽查已删——m3-jit-2 教训：它往竞技场 +4MiB/+32MiB/尾部写 "JUNK"
    // 标记做别名一致性验证，而这份竞技场随后整个交给 runtime 按偏移分配，
    // 引擎分配越过 4MiB 后标记落进活结构的指针槽——.ips 的故障地址
    // 0x4a554e4d 正是 "JUNK"+2 拼出来的假指针。一致性本来就已被探针 #1/#2
    // 端到端证明：经 RW 别名写机器码、经竞技场本体执行成功即是。M1 探针
    // 时代 Hello.dll 分配永远到不了 4MiB，所以这颗雷从没被踩响过。）
    char rx[32], rwb[32], szb[32], mmrx[32], mmrw[32], mmsz[32];
    snprintf(rx, sizeof(rx), "%llx", (unsigned long long)(uintptr_t)arena);
    snprintf(rwb, sizeof(rwb), "%llx", (unsigned long long)(uintptr_t)rwAlias);
    snprintf(szb, sizeof(szb), "%llx", RW17_CAP - RW17_MM_CAP);
    snprintf(mmrx, sizeof(mmrx), "%llx", (unsigned long long)(uintptr_t)arena + RW17_CAP - RW17_MM_CAP);
    snprintf(mmrw, sizeof(mmrw), "%llx", (unsigned long long)(uintptr_t)rwAlias + RW17_CAP - RW17_MM_CAP);
    snprintf(mmsz, sizeof(mmsz), "%llx", RW17_MM_CAP);
    setenv("PORT17_JIT_RX", rx, 1);
    setenv("PORT17_JIT_RW", rwb, 1);
    setenv("PORT17_JIT_SIZE", szb, 1);
    setenv("PORT17_MM_RX", mmrx, 1);
    setenv("PORT17_MM_RW", mmrw, 1);
    setenv("PORT17_MM_SIZE", mmsz, 1);
    snprintf(buf, sizeof(buf), "[jit26] 竞技场就绪并已交给 runtime（rx=%s rw=%s size=%s）；MonoMod 切片 rx=%s rw=%s size=%s",
             rx, rwb, szb, mmrx, mmrw, mmsz);
    host_log(buf);
    return 1;
}
