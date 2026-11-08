#include "host.h"

#include <dlfcn.h>
#include <dirent.h>
#include <mach/mach.h>
#include <mach/mach_vm.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/errno.h>

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

// 匿名（SM_PRIVATE）且含执行权限的内存区域 = JIT 分配的可执行内存。
// 解释器模式下应该几乎没有；JIT 开启后 CoreCLR 的双映射（RW+RX）会计入。
static void scan_exec_regions(const char *when)
{
    mach_vm_address_t addr = MACH_VM_MIN_ADDRESS;
    mach_vm_size_t size;
    vm_region_recurse_info_data_64_t info;
    mach_msg_type_number_t count = VM_REGION_RECURSE_INFO_COUNT_64;
    natural_t depth = 0;
    uint64_t total = 0;
    uint64_t largest = 0;
    uint64_t nregions = 0;
    while (mach_vm_region_recurse(mach_task_self(), &addr, &size, &depth,
                                  (vm_region_recurse_info_t)&info, &count) ==
           KERN_SUCCESS) {
        if ((info.max_protection & VM_PROT_EXECUTE) != 0 &&
            info.share_mode == SM_PRIVATE) {
            total += size;
            if (size > largest)
                largest = size;
            nregions++;
        }
        count = VM_REGION_RECURSE_INFO_COUNT_64;
        addr += size;
        if (addr == 0)
            break;
    }
    char buf[256];
    snprintf(buf, sizeof(buf),
             "匿名可执行内存（%s）：%llu 个区域，共 %llu KiB，最大 %llu KiB",
             when, (unsigned long long)nregions, (unsigned long long)(total / 1024),
             (unsigned long long)(largest / 1024));
    host_log(buf);
}

int run_probe(const char *bundle_dir)
{
    char *managed_dir = path_join(bundle_dir, "managed");
    char *dylib_path = path_join(bundle_dir, "libcoreclr.dylib");

    void *clr = dlopen(dylib_path, RTLD_NOW);
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
    const char *keys[3] = {"TRUSTED_PLATFORM_ASSEMBLIES", "APP_PATHS",
                           "APP_NI_PATHS"};
    const char *values[3] = {tpa, managed_dir, managed_dir};

    host_log("初始化 CoreCLR…");
    void *host = NULL;
    unsigned int domain = 0;
    int hr = init(bundle_dir, "Port17Probe", 3, keys, values, &host, &domain);
    if (hr != 0) {
        char buf[64];
        snprintf(buf, sizeof(buf), "coreclr_initialize 失败：0x%08x", hr);
        host_log(buf);
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
