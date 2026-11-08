// macos-debug：官方 osx 包 + 同款宿主调用，自动二分。
// 本轮学到的契约：① exePath = 原生宿主可执行文件路径（hostpolicy 传 host_path）；
// ② 运行时报错走 coreclr_set_error_writer，不设就完全静默。
// 用法：macprobe <bundle_dir>
#include "host.h"
#include <dirent.h>
#include <dlfcn.h>
#include <libgen.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void sink(const char *msg) { printf("[macprobe] %s\n", msg); }

static char *pj(const char *dir, const char *name)
{
    size_t n = strlen(dir) + 1 + strlen(name) + 1;
    char *p = (char *)malloc(n);
    snprintf(p, n, "%s/%s", dir, name);
    return p;
}

static char *build_tpa(const char *dir)
{
    DIR *d = opendir(dir);
    if (d == NULL)
        return NULL;
    size_t cap = 65536, len = 0;
    char *tpa = (char *)malloc(cap);
    tpa[0] = '\0';
    struct dirent *e;
    while ((e = readdir(d)) != NULL) {
        size_t n = strlen(e->d_name);
        if (n < 5 || strcmp(e->d_name + n - 4, ".dll") != 0)
            continue;
        char *full = pj(dir, e->d_name);
        while (len + strlen(full) + 2 > cap) {
            cap *= 2;
            tpa = (char *)realloc(tpa, cap);
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

typedef void (*set_error_writer_fn)(void (*)(const char *));
typedef int (*init_fn)(const char *, const char *, int, const char **,
                       const char **, void **, unsigned int *);
typedef int (*exec_fn)(void *, unsigned int, int, char **, const char *,
                       unsigned int *);

int main(int argc, char **argv)
{
    setbuf(stdout, NULL); // 段错误时不丢日志
    host_set_log_sink(sink);
    const char *dir = argv[1];
    char self[4096];
    // exePath 契约：宿主可执行文件自己的路径（hostpolicy 传 host_path）
    if (!realpath(argv[0], self)) {
        printf("realpath 失败\n");
        return 2;
    }
    const char *managed = dir; // 拍平布局：托管 dll 与 dylib 同目录
    char *hello = pj(dir, "Hello.dll");

    void *lib = dlopen(pj(dir, "libcoreclr.dylib"), RTLD_NOW | RTLD_GLOBAL);
    if (!lib) {
        printf("dlopen 失败: %s\n", dlerror());
        return 2;
    }
    init_fn init = (init_fn)dlsym(lib, "coreclr_initialize");
    exec_fn exec = (exec_fn)dlsym(lib, "coreclr_execute_assembly");
    set_error_writer_fn set_err =
        (set_error_writer_fn)dlsym(lib, "coreclr_set_error_writer");
    if (set_err)
        set_err(sink); // 关键：让运行时报错可见
    else
        printf("没有 coreclr_set_error_writer 导出\n");
    char *tpa = build_tpa(managed);
    if (!tpa) {
        printf("managed 目录不可读\n");
        return 2;
    }

    const char *kA[] = {"TRUSTED_PLATFORM_ASSEMBLIES", "APP_PATHS",
                        "APP_NI_PATHS", "NATIVE_DLL_SEARCH_DIRECTORIES",
                        "APP_CONTEXT_BASE_DIRECTORY"};
    const char *vA[] = {tpa, managed, managed, (char *)dir, (char *)dir};
    const char *kB[] = {"TRUSTED_PLATFORM_ASSEMBLIES"};
    const char *vB[] = {tpa};

    struct {
        const char *name;
        const char *exe;
        const char **k;
        const char **v;
        int n;
    } variants[] = {
        {"A exePath=宿主可执行 + 全属性（hostpolicy 语义）", self, kA, vA, 5},
        {"B exePath=宿主可执行 + 仅 TPA", self, kB, vB, 1},
        {"C exePath=Hello.dll（此前一直的传法）", hello, kA, vA, 5},
    };

    for (size_t i = 0; i < sizeof(variants) / sizeof(variants[0]); i++) {
        printf("[macprobe] ---- %s ----\n", variants[i].name);
        void *host = NULL;
        unsigned int domain = 0;
        int hr = init(variants[i].exe, "macdebug", variants[i].n,
                      variants[i].k, variants[i].v, &host, &domain);
        if (hr != 0) {
            printf("[macprobe] → 失败 0x%08x\n", hr);
            continue;
        }
        printf("[macprobe] → 初始化成功！执行 Hello…\n");
        char *cargv[1] = {hello};
        unsigned int exit_code = 0;
        hr = exec(host, domain, 1, cargv, hello, &exit_code);
        printf("[macprobe] execute_assembly hr=0x%08x exit=%u\n", hr,
               exit_code);
        return hr == 0 ? 0 : 1;
    }
    printf("[macprobe] 所有变体都失败\n");
    return 1;
}
