#include "host.h"

#include <dlfcn.h>
#include <dirent.h>
#include <mach/mach.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/errno.h>
#include <sys/stat.h>

// mach_vm_region_recurse 的头文件 mach/mach_vm.h 在 iOS SDK 里被 #error 挡住；
// 用 vm_region_64 + VM_REGION_BASIC_INFO_64——CoreCLR PAL 的 map/virtual.cpp 在
// iOS 构建里就用这一组合。iOS 的 basic_info 是精简版（没有 share_mode），所以
// 不区分匿名与否，改为报告可执行区域总量，由执行前后的差值看新分配的执行内存。

typedef int (*coreclr_initialize_fn)(
    const char *exePath, const char *appDomainName, int propertyCount,
    const char **propertyKeys, const char **propertyValues, void **hostHandle,
    unsigned int *domainId);
typedef int (*coreclr_execute_assembly_fn)(
    void *hostHandle, unsigned int argc, char **argv,
    const char *managedAssemblyPath, unsigned int *exitCode);

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
    char *managed_dir = path_join(bundle_dir, "managed");
    char *dylib_path = path_join(bundle_dir, "libcoreclr.dylib");
    char *hello_path = path_join(managed_dir, "Hello.dll");

    void *clr = dlopen(dylib_path, RTLD_NOW | RTLD_GLOBAL);
    if (clr == NULL) {
        host_log(dlerror());
        return -1;
    }
    coreclr_initialize_fn init =
        (coreclr_initialize_fn)dlsym(clr, "coreclr_initialize");
    coreclr_execute_assembly_fn exec =
        (coreclr_execute_assembly_fn)dlsym(clr, "coreclr_execute_assembly");
    if (init == NULL || exec == NULL) {
        host_log("找不到 coreclr_initialize / coreclr_execute_assembly");
        return -2;
    }

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
    }

    // 一次跑一组初始化变体（真机上 0x80070002 = FILE_NOT_FOUND 的来源定位）：
    // A 基线；B exePath 用程序集文件路径（安卓版 host.c 的做法）；C 再叠安卓版的关键属性；
    // D 再叠全球化 Invariant（Apple mobile 是 hybrid globalization，文化数据缺失也可能是
    // FILE_NOT_FOUND 的来源）。
    void *host = NULL;
    unsigned int domain = 0;
    int hr = -1;

    const char *keysA[3] = {"TRUSTED_PLATFORM_ASSEMBLIES", "APP_PATHS",
                            "APP_NI_PATHS"};
    const char *valuesA[3] = {tpa, managed_dir, managed_dir};

    const char *keysC[5] = {"TRUSTED_PLATFORM_ASSEMBLIES", "APP_PATHS",
                            "APP_NI_PATHS", "NATIVE_DLL_SEARCH_DIRECTORIES",
                            "APP_CONTEXT_BASE_DIRECTORY"};
    const char *valuesC[5] = {tpa, managed_dir, managed_dir, (char *)bundle_dir,
                              (char *)bundle_dir};

    const char *keysD[7] = {"TRUSTED_PLATFORM_ASSEMBLIES", "APP_PATHS",
                            "APP_NI_PATHS", "NATIVE_DLL_SEARCH_DIRECTORIES",
                            "APP_CONTEXT_BASE_DIRECTORY",
                            "System.Globalization.Invariant",
                            "System.Globalization.PredefinedCulturesOnly"};
    const char *valuesD[7] = {tpa, managed_dir, managed_dir, (char *)bundle_dir,
                              (char *)bundle_dir, "true", "false"};

    struct {
        const char *name;
        const char *exe;
        const char **keys;
        const char **values;
        int count;
    } variants[] = {
        {"A 基线（目录作 exePath）", bundle_dir, keysA, valuesA, 3},
        {"B exePath=程序集文件", hello_path, keysA, valuesA, 3},
        {"C +搜索目录/基目录", hello_path, keysC, valuesC, 5},
        {"D +全球化 Invariant", hello_path, keysD, valuesD, 7},
    };

    for (size_t i = 0; i < sizeof(variants) / sizeof(variants[0]); i++) {
        char buf[96];
        snprintf(buf, sizeof(buf), "尝试 %s…", variants[i].name);
        host_log(buf);
        hr = init(variants[i].exe, "Port17Probe", variants[i].count,
                  variants[i].keys, variants[i].values, &host, &domain);
        if (hr == 0) {
            host_log("→ 该变体成功");
            break;
        }
        snprintf(buf, sizeof(buf), "→ 失败：0x%08x", hr);
        host_log(buf);
        host = NULL;
    }
    if (hr != 0) {
        host_log("所有初始化变体都失败");
        return -4;
    }
    host_log("CoreCLR 就绪");

    scan_exec_regions("执行前");

    char *hello_path = path_join(managed_dir, "Hello.dll");
    char *argv[1] = {hello_path};
    unsigned int exit_code = 0;
    hr = exec(host, 1, argv, hello_path, &exit_code);
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
