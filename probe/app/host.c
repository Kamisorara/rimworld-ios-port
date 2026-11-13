#include "host.h"

#include <TargetConditionals.h>
#include <dlfcn.h>
#include <dirent.h>
#include <mach/mach.h>
#include <libkern/OSCacheControl.h>
#include <pthread.h>
#include <setjmp.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdbool.h>
#include <sys/errno.h>
#include <sys/stat.h>
#include <sys/mman.h>
#include <sys/sysctl.h>
#include <time.h>
#include <unistd.h>

// mach_vm_region_recurse 的头文件 mach/mach_vm.h 在 iOS SDK 里被 #error 挡住；
// 用 vm_region_64 + VM_REGION_BASIC_INFO_64——CoreCLR PAL 的 map/virtual.cpp 在
// iOS 构建里就用这一组合。iOS 的 basic_info 是精简版（没有 share_mode），所以
// 不区分匿名与否，改为报告可执行区域总量，由执行前后的差值看新分配的执行内存。

typedef int (*coreclr_initialize_fn)(
    const char *exePath, const char *appDomainName, int propertyCount,
    const char **propertyKeys, const char **propertyValues, void **hostHandle,
    unsigned int *domainId);
typedef int (*coreclr_execute_assembly_fn)(
    void *hostHandle, unsigned int domainId, int argc, char **argv,
    const char *managedAssemblyPath, unsigned int *exitCode);
typedef void (*coreclr_set_error_writer_fn)(void (*)(const char *message));

static void (*g_log_sink)(const char *) = NULL;

void host_set_log_sink(void (*sink)(const char *)) { g_log_sink = sink; }

void host_log(const char *msg)
{
    if (g_log_sink != NULL)
        g_log_sink(msg);
    fprintf(stderr, "[probe] %s\n", msg); // 同时进系统日志，idevicesyslog 可见
}

static char *path_join(const char *dir, const char *name)
{
    size_t n = strlen(dir) + 1 + strlen(name) + 1;
    char *p = (char *)malloc(n);
    snprintf(p, n, "%s/%s", dir, name);
    return p;
}

// 把 managed 目录里的托管程序集拼成 TPA（冒号分隔）
static char *build_tpa(const char *managed_dir)
{
    DIR *d = opendir(managed_dir);
    if (d == NULL)
        return NULL;
    size_t cap = 4096, len = 0;
    char *tpa = (char *)malloc(cap);
    tpa[0] = '\0';
    struct dirent *e;
    while ((e = readdir(d)) != NULL) {
        size_t n = strlen(e->d_name);
        if (n < 5 || strcmp(e->d_name + n - 4, ".dll") != 0)
            continue;
        char *full = path_join(managed_dir, e->d_name);
        size_t need = len + strlen(full) + 2;
        if (need > cap) {
            cap = need * 2;
            char *grown = (char *)realloc(tpa, cap);
            if (grown == NULL) { free(full); closedir(d); free(tpa); return NULL; }
            tpa = grown;
        }
        if (len > 0)
            tpa[len++] = ':';
        strcpy(tpa + len, full);
        len += strlen(full);
        free(full);
    }
    closedir(d);
    return tpa;
}

// 统计含执行权限的内存区域（含系统映射）。执行前后各扫一次，差值即新分配的
// 执行内存：JIT 开启时差值明显（CoreCLR 代码堆数十 MB），解释器模式下几乎为零。
static void scan_exec_regions(const char *when)
{
    vm_address_t addr = 0;
    vm_size_t size;
    vm_region_basic_info_data_64_t info;
    mach_msg_type_number_t count;
    mach_port_t object_name;
    uint64_t total = 0;
    uint64_t nregions = 0;
    while (1) {
        count = VM_REGION_BASIC_INFO_COUNT_64;
        if (vm_region_64(mach_task_self(), &addr, &size, VM_REGION_BASIC_INFO_64,
                         (vm_region_info_t)&info, &count,
                         &object_name) != KERN_SUCCESS)
            break;
        if ((info.max_protection & VM_PROT_EXECUTE) != 0) {
            total += size;
            nregions++;
        }
        if (addr + size <= addr)
            break; // 溢出保护：扫完全部区域
        addr += size;
    }
    char buf[256];
    snprintf(buf, sizeof(buf),
             "可执行内存（%s）：%llu 个区域，共 %llu KiB",
             when, (unsigned long long)nregions,
             (unsigned long long)(total / 1024));
    host_log(buf);
}

// JIT 竞技场不再由本进程等待/扫描：runtime 补丁（patches/dualjit-arena.patch）
// 的 JIT26 后端在 coreclr_initialize 里自己发 brk #0xf00d 请求，StikDebug 的
// rw17-jit26.js 应答（分配 RX + 逐页祝福）后立即分离——启动竞态随协议消失，
// 本文件只剩诊断与 CoreCLR 装载。

// 把 stderr 强制落盘并逐行读回屏幕日志。Darwin libc 里 freopen 后的 stderr 是
// 全缓冲，本应用不退出就永不落盘——coreclr-stderr.log 一直是 0 字节的根因
//（main.m 已加 setvbuf 关缓冲，这里再 fflush 兜底，保证 [rw17-arena] 上屏）。
// 注意先整体读入快照再回放：host_log 每行也写回 stderr（同一个文件），
// 边读边写会让文件增长快过读取，逐行读永远追不上 EOF（死循环）。
static void dump_coreclr_stderr(void)
{
    fflush(stderr);
    const char *home = getenv("HOME");
    if (home == NULL) {
        host_log("（HOME 未设置，无法读回 coreclr-stderr.log）");
        return;
    }
    char path[512];
    snprintf(path, sizeof(path), "%s/Documents/coreclr-stderr.log", home);
    FILE *f = fopen(path, "r");
    if (f == NULL) {
        host_log("（coreclr-stderr.log 打不开——stderr 重定向本身失败了）");
        return;
    }
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    rewind(f);
    if (size > 64 * 1024)
        size = 64 * 1024; // 防御上限：日志回放不拖垮 UI
    char *data = malloc((size_t)size + 1);
    if (data == NULL) {
        fclose(f);
        return;
    }
    size_t got = fread(data, 1, (size_t)size, f);
    fclose(f);
    data[got] = '\0';
    host_log("---- CoreCLR stderr 开始 ----");
    char *p = data;
    while (*p != '\0') {
        char *nl = strchr(p, '\n');
        if (nl != NULL)
            *nl = '\0';
        host_log(p);
        if (nl == NULL)
            break;
        p = nl + 1;
    }
    free(data);
    host_log("---- CoreCLR stderr 结束 ----");
}

// ---- stderr 跟随线程：[rw17-arena] 进度实时上屏 ----
// runtime 的 [rw17-arena] 行只写 stderr 文件；屏幕日志此前只在
// coreclr_initialize 失败返回后才回放（dump_coreclr_stderr 在 hr!=0 分支）
// ——初始化若卡住，屏幕停在"初始化 CoreCLR…"，分不清正常等待（等脚本
// ≤120 秒 + 逐页扫写 1–2 分钟）还是真死。此线程每 2 秒把文件里新增的
// 完整 [rw17-arena] 行转 host_log 上屏。
// 只转发该前缀：host_log 会把 "[probe] ..." 行写回同一个文件，不转发
// 它们就永远不会把自己刚打过的行再读出来打一遍（自激死循环的根）。
static volatile int g_tailer_run = 0;
static pthread_t g_tailer_tid;

static void *stderr_tailer_main(void *unused)
{
    (void)unused;
    const char *home = getenv("HOME");
    if (home == NULL)
        return NULL;
    char path[512];
    snprintf(path, sizeof(path), "%s/Documents/coreclr-stderr.log", home);
    FILE *f = fopen(path, "r");
    if (f == NULL)
        return NULL;
    char pending[1024];
    size_t pendingLen = 0;
    while (g_tailer_run) {
        char buf[2048];
        size_t got = fread(buf, 1, sizeof(buf), f);
        clearerr(f); // 文件增长后 stdio 的 EOF 指示要清掉才能继续读
        size_t pos = 0;
        while (pos < got) {
            char *nl = memchr(buf + pos, '\n', got - pos);
            size_t chunk = (nl != NULL) ? (size_t)(nl - (buf + pos)) + 1 : got - pos;
            if (pendingLen + chunk < sizeof(pending)) {
                memcpy(pending + pendingLen, buf + pos, chunk);
                pendingLen += chunk;
            } else {
                pendingLen = 0; // 异常长的行直接丢弃（[rw17-arena] 行都很短）
            }
            pos += chunk;
            if (nl != NULL) {
                if (pendingLen > 0)
                    pending[pendingLen - 1] = '\0';
                else
                    pending[0] = '\0';
                if (strncmp(pending, "[rw17-arena]", 12) == 0)
                    host_log(pending);
                pendingLen = 0;
            }
        }
        struct timespec ts = {2, 0}; // 2 秒一轮，够实时也不抢 CPU
        nanosleep(&ts, NULL);
    }
    fclose(f);
    return NULL;
}

static void stderr_tailer_start(void)
{
    if (g_tailer_run)
        return;
    g_tailer_run = 1;
    if (pthread_create(&g_tailer_tid, NULL, stderr_tailer_main, NULL) != 0)
        g_tailer_run = 0;
}

static void stderr_tailer_stop(void)
{
    if (!g_tailer_run)
        return;
    g_tailer_run = 0;
    pthread_join(g_tailer_tid, NULL);
}

// ---- JIT26 竞技场获取（playport 所有权模型：宿主拿内存，runtime 消费）----
// 全流程在本进程内：活调试器门（CS_DEBUGGED+P_TRACED，fail-closed）→ 最多
// 120 秒等待 → brk 请求（x0=NULL、x1=容量；脚本经 debugserver _M,rx 分配并
// 逐页祝福、x0 回传地址）→ vm_remap RW 别名（mprotect / vm_protect 双路）→
// brk 分离 → 执行探针（此时无调试器，fault 归本进程）→ 通过后写
// PORT17_JIT_RX/RW/SIZE 三个环境变量交给 CoreCLR（runtime 补丁只做指针
// 消费、零 OS 调用——补丁从此稳定，宿主侧迭代不再触发 runtime 重编）。
// 双参蹦床是协议关键：单参会把容量错放进 x0，脚本的 x0==NULL 校验当场抛错
//（真机二测的教训——"arena granted at 0x8000000" 就是残留在 x0 里的容量，
// 拿一个从未分配的地址去做 vm_remap，别名自然失败）。
// 注意平台判断必须用值判断：TargetConditionals 无条件定义
// TARGET_OS_SIMULATOR（真机上为 0），defined() 会把真机当模拟器。

#define RW17_CAP 0x8000000ull // 128 MiB，与 rw17-jit26.js 的 _M 尺寸一致（x1 携带）

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
    host_log("[jit26] 等待活调试器——去 StikDebug 运行 probe/stikdebug/rw17-jit26.js");
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

static bool jit26_acquire_arena(void)
{
    char buf[160];
    if (!jit26_wait_debugger(120))
        return false;
    // 发 brk 前紧贴着再查一次（playport 纪律：会话可在等待与 brk 之间消失）
    if (!jit26_debugger_attached()) {
        host_log("[jit26] 调试器在发 brk 前消失，放弃（无人接的 brk = SIGTRAP）");
        return false;
    }
    host_log("[jit26] prepare brk 发出（128 MiB；本进程停在 brk 上，脚本逐页扫写约 1–2 分钟）");
    void *arena = jit26_prepare_region(NULL, (size_t)RW17_CAP);
    if (arena == NULL) {
        host_log("[jit26] prepare 返回 NULL，无可用 JIT 内存");
        return false;
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
        return false;
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
        return false;
    }
    // 深页抽查：vm_region 只报 16KiB 子图入口（"内核视角 size=16384"），但
    // vm_remap(copy=FALSE) 是整段 128MiB 共享映射、脚本逐页扫写覆盖全部
    // 8192 页——对别名深处写、竞技场深处读回抽查，把"深处实际没映射"的
    // 怀疑直接排除或坐实。
    {
        static const size_t offs[] = {0x400000, 0x2000000, 0x7ff0000}; // +4MiB、+32MiB、尾部-64KiB
        const uint32_t mark = 0x4a554e4bu; // "JUNK"
        for (int i = 0; i < 3; i++) {
            memcpy(rwAlias + offs[i], &mark, sizeof(mark));
            uint32_t back = 0;
            memcpy(&back, (uint8_t *)arena + offs[i], sizeof(mark));
            snprintf(buf, sizeof(buf), "[jit26] 深页 0x%llx：别名写/竞技场读 %s",
                     (unsigned long long)offs[i], back == mark ? "一致" : "不一致！");
            host_log(buf);
        }
    }
    char rx[32], rwb[32], szb[32];
    snprintf(rx, sizeof(rx), "%llx", (unsigned long long)(uintptr_t)arena);
    snprintf(rwb, sizeof(rwb), "%llx", (unsigned long long)(uintptr_t)rwAlias);
    snprintf(szb, sizeof(szb), "%llx", RW17_CAP);
    setenv("PORT17_JIT_RX", rx, 1);
    setenv("PORT17_JIT_RW", rwb, 1);
    setenv("PORT17_JIT_SIZE", szb, 1);
    snprintf(buf, sizeof(buf), "[jit26] 竞技场就绪并已交给 runtime（rx=%s rw=%s size=%s）", rx, rwb, szb);
    host_log(buf);
    return true;
}

int run_probe(const char *bundle_dir)
{
    // 拍平布局：托管 dll 与 libcoreclr.dylib 同在 .app 根目录——CoreCLR 初始化
    // 按 libcoreclr 所在目录找 System.Private.CoreLib.dll（不走 TPA），子目录布局
    // 会让初始化以 0x80070002 静默失败。
    char *managed_dir = strdup(bundle_dir);
    char *dylib_path = path_join(bundle_dir, "libcoreclr.dylib");
    char *hello_path = path_join(bundle_dir, "Hello.dll");

    void *clr = dlopen(dylib_path, RTLD_NOW | RTLD_GLOBAL);
    if (clr == NULL) {
        host_log(dlerror());
        return -1;
    }
    coreclr_initialize_fn init =
        (coreclr_initialize_fn)dlsym(clr, "coreclr_initialize");
    coreclr_execute_assembly_fn exec =
        (coreclr_execute_assembly_fn)dlsym(clr, "coreclr_execute_assembly");
    coreclr_set_error_writer_fn set_err =
        (coreclr_set_error_writer_fn)dlsym(clr, "coreclr_set_error_writer");
    if (init == NULL || exec == NULL) {
        host_log("找不到 coreclr_initialize / coreclr_execute_assembly");
        return -2;
    }
    if (set_err != NULL)
        set_err(host_log); // 运行时报错走 error writer，不设就完全静默

    char *tpa = build_tpa(managed_dir);
    if (tpa == NULL) {
        host_log("managed 目录不可读");
        return -3;
    }

    // 诊断：把关键事实打到屏幕上
    {
        char buf[256];
        snprintf(buf, sizeof(buf), "TPA 长度 %zu，首 80 字符：%.80s",
                 strlen(tpa), tpa);
        host_log(buf);
        struct stat st;
        snprintf(buf, sizeof(buf), "CoreLib dll：%s",
                 stat(path_join(managed_dir, "System.Private.CoreLib.dll"), &st) == 0
                     ? "存在" : "缺失");
        host_log(buf);
        snprintf(buf, sizeof(buf), "CoreLib 原生 dylib：%s",
                 stat(path_join(bundle_dir, "System.Private.CoreLib.dylib"), &st) == 0
                     ? "存在" : "缺失");
        host_log(buf);

        // JIT 授权状态探测（与 runtime doublemapping.cpp 同款判定）：
        // RWX 可映射 = 调试器 attach 授权生效；仅 MAP_JIT 可映射 = 有动态代码授权；
        // 都不行 = 本次运行没有 JIT 授权（StikDebug 未开或未生效）。
        void *rwx = mmap(NULL, 4096, PROT_READ | PROT_WRITE | PROT_EXEC,
                         MAP_ANON | MAP_PRIVATE, -1, 0);
        bool rwx_ok = false;
        if (rwx != MAP_FAILED) {
            host_log("授权探测：RWX 映射成功（attach 授权生效）");
            munmap(rwx, 4096);
            rwx_ok = true;
        } else {
            snprintf(buf, sizeof(buf), "授权探测：RWX 被拒（errno=%d）", errno);
            host_log(buf);
            void *mj = mmap(NULL, 4096, PROT_READ | PROT_WRITE | PROT_EXEC,
                            MAP_ANON | MAP_PRIVATE | MAP_JIT, -1, 0);
            if (mj != MAP_FAILED) {
                host_log("授权探测：MAP_JIT 映射成功（有动态代码授权）");
                munmap(mj, 4096);
            } else {
                snprintf(buf, sizeof(buf),
                         "授权探测：MAP_JIT 也被拒（errno=%d）——本次运行无 JIT 授权",
                         errno);
                host_log(buf);
            }
        }
        void *wprot = dlsym(RTLD_DEFAULT, "pthread_jit_write_protect_np");
        snprintf(buf, sizeof(buf), "写保护开关符号：%s（竞技场方案下仅作诊断）",
                 wprot != NULL ? "找到" : "未找到（iOS 正常现象）");
        host_log(buf);
        host_log("提示：经 StikDebug 拉起本应用（带 JIT）并运行 probe/stikdebug/rw17-jit26.js（应答式脚本）");
        host_log("提示：本进程自己完成 brk 握手 + RW 别名 + 分离 + 探针（最多等调试器 120 秒），通过后把竞技场交给 CoreCLR");
        (void)rwx_ok; // 授权探测仅作诊断显示；竞技场获取在宿主侧完成，无需本地 RWX 授权
    }

#if TARGET_OSX || TARGET_OS_SIMULATOR
    // macOS/模拟器：宿主不获取竞技场——macOS 走 runtime 的种子扫描/RWX 路径，
    // 模拟器无脚本可应答、快速干净降级。值判断勿用 defined()（TargetConditionals
    // 无条件定义 TARGET_OS_SIMULATOR，真机上为 0——真机首测的教训）。
#else
    jit26_acquire_arena();
#endif

    // 正式属性组（对照安卓版 host.c 与官方 ios 运行时包布局）：
    // libSystem.Native 等类库原生 dylib 在 bundle 根目录，原生搜索目录指向那里，
    // 类库按名字 dlopen 失败后会用这里给的路径按全路径重试。
    // JIT 内存由 runtime 补丁（patches/dualjit-arena.patch）的 JIT26 后端接管：
    // runtime 在初始化里 brk 请求，StikDebug 的 rw17-jit26.js 应答（_M 分配 +
    // 逐页祝福），拿到 vm_remap RW 别名后立即分离——初始化与游戏全程无调试器
    // 附着，fault 归本进程自己的 handler。
    // 注：System.Runtime.EnableWriteXorExecute 在 Apple arm64 上是死配置
    //（IsDoubleMappingEnabled/IsWXORXEnabled 曾硬编码），不再设置。
    const char *keys[7] = {
        "TRUSTED_PLATFORM_ASSEMBLIES",
        "APP_PATHS",
        "APP_NI_PATHS",
        "NATIVE_DLL_SEARCH_DIRECTORIES",
        "APP_CONTEXT_BASE_DIRECTORY",
        "RUNTIME_IDENTIFIER",
        "STARTUP_HOOKS",
    };
    const char *values[7] = {tpa, managed_dir, managed_dir, (char *)bundle_dir,
                             (char *)bundle_dir, "ios-arm64", ""};

    // 崩溃自取证：CoreCLR 内部 Fatal error + SIGBUS 只有 PAL 的通用文案，
    // 开启 crash report 让它把 native backtrace 直接打到 stderr（上屏+落盘）
    setenv("COMPlus_EnableCrashReport", "1", 1);
    setenv("COMPlus_EnableDiagnostics", "1", 1);
    stderr_tailer_start();
    host_log("初始化 CoreCLR…（JIT26 握手在初始化内进行：等脚本 ≤120 秒 + 逐页扫写 1–2 分钟；期间 [rw17-arena] 进度实时滚动）");
    void *host = NULL;
    unsigned int domain = 0;
    // exePath 契约 = 原生宿主可执行文件路径（hostpolicy 传 host_path，不是托管 dll）
    char *host_exec = path_join(bundle_dir, "Port17Probe");
    int hr = init(host_exec, "Port17Probe", 7, keys, values, &host, &domain);
    if (hr != 0) {
        char buf[64];
        snprintf(buf, sizeof(buf), "coreclr_initialize 失败：0x%08x", hr);
        host_log(buf);
        stderr_tailer_stop();
        dump_coreclr_stderr(); // 全量回放（[rw17-arena] 已实时上屏，这里是完整留档）
        return -4;
    }
    host_log("CoreCLR 就绪");

    scan_exec_regions("执行前");

    char *argv[1] = {hello_path};
    unsigned int exit_code = 0;
    hr = exec(host, domain, 1, argv, hello_path, &exit_code);
    if (hr != 0) {
        char buf[64];
        snprintf(buf, sizeof(buf), "coreclr_execute_assembly 失败：0x%08x", hr);
        host_log(buf);
        stderr_tailer_stop();
        return -5;
    }

    scan_exec_regions("执行后");

    stderr_tailer_stop();
    char buf[96];
    snprintf(buf, sizeof(buf), "Hello 退出码：%u", exit_code);
    host_log(buf);
    return (int)exit_code;
}
