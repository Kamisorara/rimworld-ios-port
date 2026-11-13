// macos-debug：CoreCLR 宿主调试探针（官方 osx 包对照组 / 自建 osx 变体验证环）。
// 契约：① exePath = 原生宿主可执行文件路径（hostpolicy 传 host_path）；
// ② 运行时报错走 coreclr_set_error_writer，不设就完全静默。
// 用法：macprobe <bundle_dir> [variant（已弃用，仅为兼容旧 workflow 参数）]
// RW17_SEED_ARENA=1 时先在本进程伪造一块 JIT 竞技场（128MiB RX + 魔数握手头），
// 用于在 CI 的 mac 原生环境全链路验证竞技场后端（扫描/别名/探针/Commit 翻转）。
#include "host.h"
#include <dirent.h>
#include <dlfcn.h>
#include <errno.h>
#include <libgen.h>
#include <sys/mman.h>
#include <unistd.h>
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

// 伪造竞技场：魔数 + pid 与 patches/dualjit-arena.patch 的 macOS 扫描路径一致
//（仅 mac 宿主 CI 用；iOS 设备端已改走 rw17-jit26.js 的 brk 握手，不再认魔数）。
// 写完握手头后 mprotect 成 RX，模拟 debugserver "_M,rx" 产物的保护态。
static void seed_arena_if_requested(void)
{
    if (getenv("RW17_SEED_ARENA") == NULL)
        return;
    const size_t cap = 0x8000000; // 128 MiB，与运行时补丁的 Rw17ArenaCapacity 一致
    uint8_t *p = mmap(NULL, cap, PROT_READ | PROT_WRITE | PROT_EXEC,
                      MAP_ANON | MAP_PRIVATE, -1, 0);
    if (p == MAP_FAILED) {
        printf("[macprobe] seed: RWX mmap 被拒（errno=%d），无法伪造竞技场\n", errno);
        return;
    }
    memcpy(p, "RW17-JIT-ARENA1!", 16);
    uint32_t pid = (uint32_t)getpid();
    memcpy(p + 16, &pid, 4);
    if (mprotect(p, cap, PROT_READ | PROT_EXEC) == -1) {
        printf("[macprobe] seed: mprotect RX 失败（errno=%d）\n", errno);
        munmap(p, cap);
        return;
    }
    printf("[macprobe] seed: 竞技场就绪 %p（128MiB RX，pid %u）\n", (void *)p, pid);
}

int main(int argc, char **argv)
{
    setbuf(stdout, NULL); // 段错误时不丢日志
    host_set_log_sink(sink);
    if (argc < 2) {
        printf("用法: macprobe <bundle_dir>\n");
        return 2;
    }
    const char *dir = argv[1];
    (void)argc;
    (void)argv; // variant 参数已弃用：EnableWriteXorExecute 在 Apple arm64 是死配置
    char self[4096];
    // exePath 契约：宿主可执行文件自己的路径（hostpolicy 传 host_path）
    if (!realpath(argv[0], self)) {
        printf("realpath 失败\n");
        return 2;
    }
    const char *managed = dir; // 拍平布局：托管 dll 与 dylib 同目录
    char *hello = pj(dir, "Hello.dll");

    seed_arena_if_requested();

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

    // 地面真相：符号解析与授权状态
    {
        void *wprot = dlsym(RTLD_DEFAULT, "pthread_jit_write_protect_np");
        printf("[macprobe] 写保护开关 dlsym：%p\n", wprot);
        void *rwx = mmap(NULL, 4096, PROT_READ | PROT_WRITE | PROT_EXEC,
                         MAP_ANON | MAP_PRIVATE, -1, 0);
        printf("[macprobe] RWX mmap：%s\n", rwx != MAP_FAILED ? "成功" : "被拒");
        if (rwx != MAP_FAILED) munmap(rwx, 4096);
        void *mj = mmap(NULL, 4096, PROT_READ | PROT_WRITE | PROT_EXEC,
                        MAP_ANON | MAP_PRIVATE | 0x8000, -1, 0);
        printf("[macprobe] MAP_JIT mmap：%s\n", mj != MAP_FAILED ? "成功" : "被拒");
        if (mj != MAP_FAILED) munmap(mj, 4096);
    }
    char *tpa = build_tpa(managed);
    if (!tpa) {
        printf("managed 目录不可读\n");
        return 2;
    }

    // JIT 内存完全交给 runtime 补丁的竞技场后端（自动：扫描竞技场 → RWX 后备）。
    const char *kA[] = {"TRUSTED_PLATFORM_ASSEMBLIES", "APP_PATHS",
                        "APP_NI_PATHS", "NATIVE_DLL_SEARCH_DIRECTORIES",
                        "APP_CONTEXT_BASE_DIRECTORY"};
    const char *vA[] = {tpa, managed, managed, (char *)dir, (char *)dir};

    printf("[macprobe] ---- 初始化 CoreCLR（竞技场后端自动选路）----\n");
    void *host = NULL;
    unsigned int domain = 0;
    int hr = init(self, "macdebug", 5, kA, vA, &host, &domain);
    if (hr != 0) {
        printf("[macprobe] → 失败 0x%08x\n", hr);
        return 1;
    }
    printf("[macprobe] → 初始化成功！执行 Hello…\n");
    char *cargv[1] = {hello};
    unsigned int exit_code = 0;
    hr = exec(host, domain, 1, cargv, hello, &exit_code);
    printf("[macprobe] execute_assembly hr=0x%08x exit=%u\n", hr, exit_code);
    return hr == 0 ? 0 : 1;
}
