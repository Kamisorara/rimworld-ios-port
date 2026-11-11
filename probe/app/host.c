#include "host.h"

#include <dlfcn.h>
#include <dirent.h>
#include <mach/mach.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdbool.h>
#include <sys/errno.h>
#include <sys/stat.h>
#include <sys/mman.h>
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

// ---- JIT 竞技场等待（与 runtime 补丁 Rw17FindArena 同款判定）----
// rw17-jit.js 经 debugserver 在本进程里逐页扫写 128MiB（约 8 千个包，秒级），
// 本进程毫秒级就跑到 coreclr_initialize——不等就永远扫不到竞技场（启动竞态）。
// 竞技场判定：RX 且 size ≥ cap 的区域，页 0 开头是 16 字节魔数 + 4 字节小端 pid。
#define RW17_ARENA_MAGIC "RW17-JIT-ARENA1!"
#define RW17_ARENA_CAP 0x8000000u

static bool rw17_arena_header_ok(const void *base)
{
    uint8_t header[20]; // 魔数 16 + pid 4
    vm_size_t got = 0;
    if (vm_read_overwrite(mach_task_self(), (vm_address_t)base, sizeof(header),
                          (vm_address_t)header, &got) != KERN_SUCCESS ||
        got != sizeof(header))
        return false;
    if (memcmp(header, RW17_ARENA_MAGIC, 16) != 0)
        return false;
    uint32_t pid;
    memcpy(&pid, header + 16, sizeof(pid));
    return pid == (uint32_t)getpid();
}

static const void *rw17_find_arena(void)
{
    vm_address_t addr = 0;
    for (;;) {
        vm_size_t size = 0;
        vm_region_basic_info_data_64_t info;
        mach_msg_type_number_t count = VM_REGION_BASIC_INFO_COUNT_64;
        mach_port_t obj = MACH_PORT_NULL;
        if (vm_region_64(mach_task_self(), &addr, &size, VM_REGION_BASIC_INFO_64,
                         (vm_region_info_t)&info, &count, &obj) != KERN_SUCCESS ||
            size == 0)
            return NULL;
        if (obj != MACH_PORT_NULL)
            mach_port_deallocate(mach_task_self(), obj);
        if ((info.protection & VM_PROT_EXECUTE) &&
            !(info.protection & VM_PROT_WRITE) && size >= RW17_ARENA_CAP &&
            rw17_arena_header_ok((const void *)addr))
            return (const void *)addr;
        vm_address_t next = addr + size;
        if (next <= addr)
            return NULL; // 地址空间回绕
        addr = next;
    }
}

// 竞技场由 debugserver 从进程外写入：本进程挂起也不影响生成，所以这个等待
// 循环在 iOS 挂起/恢复之间来回没有副作用，用户随时可以切去 StikDebug 跑脚本。
static void wait_for_rw17_arena(void)
{
    const int kTimeoutSec = 600;
    host_log("等待 JIT 竞技场就绪（rw17-jit.js 页扫写约需 90 秒；可现在切去 StikDebug 运行）…");
    for (int waited = 0; waited <= kTimeoutSec; waited++) {
        if (rw17_find_arena() != NULL) {
            char buf[96];
            snprintf(buf, sizeof(buf), "JIT 竞技场已就绪（等待 %d 秒）", waited);
            host_log(buf);
            return;
        }
        if (waited == kTimeoutSec)
            break;
        usleep(1000 * 1000);
        if (waited > 0 && waited % 15 == 0) {
            char buf[128];
            snprintf(buf, sizeof(buf),
                     "仍在等待 JIT 竞技场…（%d/%d 秒）", waited, kTimeoutSec);
            host_log(buf);
        }
    }
    host_log("等待超时（600 秒）未见到竞技场——rw17-jit.js 未运行、未完成或挂到了别的进程；仍继续初始化（预期失败）");
}

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
        host_log("提示：先经 StikDebug 启动本应用（带 JIT），再运行 rw17-jit.js——本应用会自动等待竞技场");
        if (rwx_ok)
            wait_for_rw17_arena();
    }

    // 正式属性组（对照安卓版 host.c 与官方 ios 运行时包布局）：
    // libSystem.Native 等类库原生 dylib 在 bundle 根目录，原生搜索目录指向那里，
    // 类库按名字 dlopen 失败后会用这里给的路径按全路径重试。
    // JIT 内存由 runtime 补丁（patches/dualjit-arena.patch）里的竞技场后端接管：
    // 先在 StikDebug 运行 probe/stikdebug/rw17-jit.js 预分配 RX 竞技场，
    // 运行时扫描命中后走 vm_remap RX/RW 双视图；未命中时运行时自建 RWX 后备。
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

    host_log("初始化 CoreCLR…");
    void *host = NULL;
    unsigned int domain = 0;
    // exePath 契约 = 原生宿主可执行文件路径（hostpolicy 传 host_path，不是托管 dll）
    char *host_exec = path_join(bundle_dir, "Port17Probe");
    int hr = init(host_exec, "Port17Probe", 7, keys, values, &host, &domain);
    if (hr != 0) {
        char buf[64];
        snprintf(buf, sizeof(buf), "coreclr_initialize 失败：0x%08x", hr);
        host_log(buf);
        dump_coreclr_stderr(); // [rw17-arena] 扫描日志在这里上屏
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
        return -5;
    }

    scan_exec_regions("执行后");

    char buf[96];
    snprintf(buf, sizeof(buf), "Hello 退出码：%u", exit_code);
    host_log(buf);
    return (int)exit_code;
}
