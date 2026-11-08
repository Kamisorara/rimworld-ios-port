#include "host.h"

#include <dlfcn.h>
#include <dirent.h>
#include <mach/mach.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <dlfcn.h>
#include <sys/errno.h>
#include <sys/stat.h>
#include <sys/mman.h>

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
        if (rwx != MAP_FAILED) {
            host_log("授权探测：RWX 映射成功（attach 授权生效）");
            munmap(rwx, 4096);
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
        snprintf(buf, sizeof(buf), "写保护开关符号：%s",
                 wprot != NULL ? "找到" : "未找到（补丁需改弱引用声明）");
        host_log(buf);
    }

    // 正式属性组（对照安卓版 host.c 与官方 ios 运行时包布局）：
    // libSystem.Native 等类库原生 dylib 在 bundle 根目录，原生搜索目录指向那里，
    // 类库按名字 dlopen 失败后会用这里给的路径按全路径重试。
    // EnableWriteXorExecute=0：iOS 上 pthread_jit_write_protect_np 对 dlsym 不可见，
    // W^X 翻页开关无从生效 → 关掉 W^X，JIT 走直连 RWX 单一映射（attach 授权下可映射、可写、可执行）。
    const char *keys[8] = {
        "TRUSTED_PLATFORM_ASSEMBLIES",
        "APP_PATHS",
        "APP_NI_PATHS",
        "NATIVE_DLL_SEARCH_DIRECTORIES",
        "APP_CONTEXT_BASE_DIRECTORY",
        "RUNTIME_IDENTIFIER",
        "STARTUP_HOOKS",
        "System.Runtime.EnableWriteXorExecute",
    };
    const char *values[8] = {tpa, managed_dir, managed_dir, (char *)bundle_dir,
                             (char *)bundle_dir, "ios-arm64", "", "0"};

    host_log("初始化 CoreCLR…");
    void *host = NULL;
    unsigned int domain = 0;
    // exePath 契约 = 原生宿主可执行文件路径（hostpolicy 传 host_path，不是托管 dll）
    char *host_exec = path_join(bundle_dir, "Port17Probe");
    int hr = init(host_exec, "Port17Probe", 8, keys, values, &host, &domain);
    if (hr != 0) {
        char buf[64];
        snprintf(buf, sizeof(buf), "coreclr_initialize 失败：0x%08x", hr);
        host_log(buf);
        host_log("（CoreCLR 自己写到 stderr 的内容已附在日志末尾）");
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
