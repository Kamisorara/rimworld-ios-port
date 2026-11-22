// The in-app game patcher runner: Port17Setup.dll (setup/, Mono.Cecil inside) rewrites the
// player's freshly-copied game folder on the device — the Android SetupService's iOS twin,
// minus the separate process (iOS has no fork: this spends the process's one CoreCLR, after
// which the host drops to the diagnostics UI and the player reopens into the engine mode).
#include "host.h"

#include <CoreFoundation/CoreFoundation.h>
#include <dlfcn.h>
#include <dirent.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

typedef int (*coreclr_initialize_fn)(
    const char *exePath, const char *appDomainName, int propertyCount,
    const char **propertyKeys, const char **propertyValues, void **hostHandle,
    unsigned int *domainId);
typedef int (*coreclr_execute_assembly_fn)(
    void *hostHandle, unsigned int domainId, int argc, char **argv,
    const char *managedAssemblyPath, unsigned int *exitCode);
typedef void (*coreclr_set_error_writer_fn)(void (*)(const char *message));

// The setup's stdout, line by line to the overlay (the player watches the rewrite happen).
static void *stdout_pump(void *arg)
{
    int fd = (int)(intptr_t)arg;
    char line[2048];
    size_t n = 0;
    char buf[4096];
    for (;;)
    {
        ssize_t got = read(fd, buf, sizeof buf);
        if (got <= 0)
            break;
        for (ssize_t i = 0; i < got; i++)
        {
            char c = buf[i];
            if (c == '\n' || n == sizeof line - 1)
            {
                line[n] = '\0';
                if (n > 0)
                    host_log(line);
                n = 0;
            }
            else if (c != '\r')
            {
                line[n++] = c;
            }
        }
    }
    return NULL;
}

static int file_there(const char *path)
{
    struct stat st;
    return stat(path, &st) == 0;
}

struct setup_arg
{
    const char *bundle;
    const char *game;
    int result;
};

static int run_setup_now(const char *bundle, const char *game)
{
    char setupDll[1100], cecilDll[1100];
    snprintf(setupDll, sizeof setupDll, "%s/Port17Setup.dll", bundle);
    snprintf(cecilDll, sizeof cecilDll, "%s/Mono.Cecil.dll", bundle);
    if (!file_there(setupDll) || !file_there(cecilDll))
    {
        host_log("setup：Port17Setup.dll / Mono.Cecil.dll 不在 bundle（这个包没带 setup 件）");
        return -1;
    }

    // The setup's words go to the screen too.
    int fds[2];
    if (pipe(fds) == 0)
    {
        dup2(fds[1], 1);
        close(fds[1]);
        setvbuf(stdout, NULL, _IONBF, 0);
        pthread_t t;
        if (pthread_create(&t, NULL, stdout_pump, (void *)(intptr_t)fds[0]) == 0)
            pthread_detach(t);
        else
            close(fds[0]);
    }

    setenv("COMPlus_InterpMode", "2", 1); // the M1 contract, same line as the engine
    setenv("COMPlus_EnableCrashReport", "1", 1);

    // TPA: the bundle's runtime plus the setup and Cecil themselves.
    static char tpa[1 << 18];
    tpa[0] = '\0';
    size_t len = 0;
    DIR *d = opendir(bundle);
    if (d == NULL)
    {
        host_log("setup：bundle 目录列不了");
        return -2;
    }
    struct dirent *e;
    while ((e = readdir(d)) != NULL)
    {
        size_t n = strlen(e->d_name);
        if (n < 5 || strcmp(e->d_name + n - 4, ".dll") != 0)
            continue;
        if (strcmp(e->d_name, "Hello.dll") == 0)
            continue; // the probe's toy stays out
        snprintf(tpa + len, sizeof tpa - len, "%s%s/%s", len > 0 ? ":" : "", bundle, e->d_name);
        len = strlen(tpa);
    }
    closedir(d);

    char bundleSlash[1024];
    snprintf(bundleSlash, sizeof bundleSlash, "%s/", bundle);
    const char *keys[] = {
        "TRUSTED_PLATFORM_ASSEMBLIES",
        "APP_PATHS",
        "APP_NI_PATHS",
        "NATIVE_DLL_SEARCH_DIRECTORIES",
        "APP_CONTEXT_BASE_DIRECTORY",
        "RUNTIME_IDENTIFIER",
        "System.Globalization.Invariant",
    };
    const char *values[] = { tpa, (char *)bundle, (char *)bundle, (char *)bundle, bundleSlash, "ios-arm64", "true" };

    char coreclrPath[1100];
    snprintf(coreclrPath, sizeof coreclrPath, "%s/libcoreclr.dylib", bundle);
    void *lib = dlopen(coreclrPath, RTLD_NOW | RTLD_GLOBAL);
    if (lib == NULL)
    {
        host_log(dlerror());
        return -3;
    }
    coreclr_initialize_fn init = (coreclr_initialize_fn)dlsym(lib, "coreclr_initialize");
    coreclr_execute_assembly_fn exec = (coreclr_execute_assembly_fn)dlsym(lib, "coreclr_execute_assembly");
    coreclr_set_error_writer_fn set_err = (coreclr_set_error_writer_fn)dlsym(lib, "coreclr_set_error_writer");
    if (init == NULL || exec == NULL)
    {
        host_log("setup：coreclr 入口缺失");
        return -4;
    }
    if (set_err != NULL)
        set_err(host_log);

    char exePath[1100];
    snprintf(exePath, sizeof exePath, "%s/Port17Host", bundle);
    void *host = NULL;
    unsigned int domain = 0;
    host_log("setup：初始化 CoreCLR（游戏文件改写马上开始——完成前不要关 app）…");
    int hr = init(exePath, "port17-setup", (int)(sizeof keys / sizeof keys[0]), keys, values, &host, &domain);
    if (hr != 0)
    {
        char buf[64];
        snprintf(buf, sizeof buf, "setup：coreclr_initialize 失败 0x%08x", hr);
        host_log(buf);
        return -5;
    }
    char *argv[1] = { (char *)game };
    unsigned int exitCode = 0;
    hr = exec(host, domain, 1, argv, setupDll, &exitCode);
    if (hr != 0)
    {
        char buf[64];
        snprintf(buf, sizeof buf, "setup：execute 失败 0x%08x", hr);
        host_log(buf);
        return -6;
    }
    if (exitCode != 0)
    {
        char buf[64];
        snprintf(buf, sizeof buf, "setup：Port17Setup 返回 %u（改写失败，日志在上面）", exitCode);
        host_log(buf);
        return -7;
    }
    host_log("setup：游戏文件改写完成——请彻底关闭 app（上划划掉）再重新打开，即进引擎");
    return 0;
}

static void *setup_thread(void *argRaw)
{
    struct setup_arg *arg = (struct setup_arg *)argRaw;
    arg->result = run_setup_now(arg->bundle, arg->game);
    return NULL;
}

int port17_run_setup(const char *bundle, const char *game)
{
    struct setup_arg arg = { bundle, game, -100 };
    if (!pthread_main_np())
        return run_setup_now(bundle, game);
    pthread_t t;
    if (pthread_create(&t, NULL, setup_thread, &arg) != 0)
        return run_setup_now(bundle, game);
    while (arg.result == -100)
        CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.05, false); // keep the launch watchdog fed
    pthread_join(t, NULL);
    return arg.result;
}
