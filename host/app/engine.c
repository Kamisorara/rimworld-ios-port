// The M2 closing piece: the engine loader, the Android host.c run_runtime's iOS twin.
// The game's launcher assembly (RimWorld.dll) runs on the official CoreCLR from the bundle,
// with the game's files where the player put them through the Files app:
//   <container>/Documents/game/   RimWorld.dll, the game's assemblies, Assets.zip, Data/, Rendering/
//   <container>/Documents/game/args.txt            optional command line, one argument per line
//   <container>/Documents/game/runtime.env         NAME=value per line, set after the built-ins
//   <container>/Documents/game/runtime.properties  key=value per line, runtime properties
// The framework comes from the bundle's runtime pack; the game folder's own copies of the
// framework assemblies never make it into the TPA list (the name dedup of Android's add_assemblies).
#include "host.h"

#include <dlfcn.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
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
typedef int (*coreclr_shutdown_fn)(void *hostHandle, unsigned int domainId);
typedef void (*coreclr_set_error_writer_fn)(void (*)(const char *message));

// The tail of the engine's stderr, logged when the run ends: the runtime's last words before
// an unhandled exception or a fatal error are here (the crash handler rescues them on a signal).
static void log_stderr_tail(const char *home)
{
    char path[512];
    snprintf(path, sizeof path, "%s/Documents/coreclr-stderr.log", home);
    FILE *f = fopen(path, "r");
    if (f == NULL)
        return;
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    long start = size > 4096 ? size - 4096 : 0;
    fseek(f, start, SEEK_SET);
    static char data[4097];
    size_t got = fread(data, 1, sizeof data - 1, f);
    fclose(f);
    data[got] = '\0';
    char *begin = data; // a line cut at the head goes, unless the head is the start
    if (start > 0)
    {
        char *nl = strchr(data, '\n');
        begin = nl != NULL ? nl + 1 : data + got;
    }
    host_log("---- 引擎 stderr 尾部 ----");
    char *p = begin;
    while (p >= data && *p != '\0' && p < data + got)
    {
        char *nl = strchr(p, '\n');
        if (nl != NULL)
            *nl = '\0';
        if (*p != '\0')
            host_log(p);
        if (nl == NULL)
            break;
        p = nl + 1;
    }
    host_log("---- stderr 尾部完 ----");
}

// The engine's stdout, line by line to the overlay (and so to port17-ui.log): what the game
// and the runtime print is the progress report of the boot — Android pumps it to logcat the
// same way. host_log writes to stderr, not stdout, so nothing here loops back.
static void *stdout_pump(void *arg)
{
    int fd = (int)(intptr_t)arg;
    char line[4096];
    size_t n = 0;
    char buf[8192];
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

static void redirect_stdio(const char *home)
{
    char path[512];
    snprintf(path, sizeof path, "%s/Documents/coreclr-stderr.log", home);
    freopen(path, "w", stderr); // the file the crash handler rescues
    setvbuf(stderr, NULL, _IONBF, 0);
    int fds[2];
    if (pipe(fds) != 0)
        return;
    dup2(fds[1], 1);
    close(fds[1]);
    setvbuf(stdout, NULL, _IONBF, 0);
    pthread_t t;
    if (pthread_create(&t, NULL, stdout_pump, (void *)(intptr_t)fds[0]) == 0)
        pthread_detach(t);
    else
        close(fds[0]);
}

// Lines from a small text file, as list entries (storage holds the strings). '#' and empty
// lines are nothing; trailing spaces and line ends are cut. Android's read_args.
static int read_lines(const char *path, const char **out, int max, char *storage, size_t cap)
{
    FILE *f = fopen(path, "r");
    if (f == NULL)
        return 0;
    int count = 0;
    size_t used = 0;
    char line[512];
    while (count < max && fgets(line, sizeof line, f) != NULL)
    {
        size_t n = strlen(line);
        while (n > 0 && (line[n - 1] == '\n' || line[n - 1] == '\r' || line[n - 1] == ' '))
            line[--n] = '\0';
        if (n == 0 || line[0] == '#')
            continue;
        if (used + n + 1 > cap)
            break;
        memcpy(storage + used, line, n + 1);
        out[count++] = storage + used;
        used += n + 1;
    }
    fclose(f);
    return count;
}

static int file_exists(const char *path)
{
    struct stat st;
    return stat(path, &st) == 0;
}

// The game folder as the host understands it: <home>/Documents/game with RimWorld.dll in it.
// A folder unpacked by hand often sits one level deep (iOS's own unzip wraps everything in a
// folder named after the zip), so a single wrapper below game/ — or a game folder under
// another name directly in Documents (port17-game, game-staging…) — is accepted too. Returns
// 0 with the found folder in out; negative when nothing unambiguous is there.
int port17_find_game(const char *home, char *out, size_t cap)
{
    char direct[1100];
    snprintf(direct, sizeof direct, "%s/Documents/game/RimWorld.dll", home);
    if (file_exists(direct))
    {
        snprintf(out, cap, "%s/Documents/game", home);
        return 0;
    }
    // One wrapper level: <dir>/<one folder>/RimWorld.dll for dir = Documents/game, then Documents.
    static const char *const scan[] = { "Documents/game", "Documents" };
    for (size_t i = 0; i < sizeof scan / sizeof scan[0]; i++)
    {
        char scanDir[1100];
        snprintf(scanDir, sizeof scanDir, "%s/%s", home, scan[i]);
        DIR *dir = opendir(scanDir);
        if (dir == NULL)
            continue;
        char found[1100];
        int hits = 0;
        struct dirent *e;
        while ((e = readdir(dir)) != NULL)
        {
            if (e->d_name[0] == '.')
                continue;
            char probe[1300];
            snprintf(probe, sizeof probe, "%s/%s/RimWorld.dll", scanDir, e->d_name);
            if (file_exists(probe))
            {
                hits++;
                snprintf(found, sizeof found, "%s/%s", scanDir, e->d_name);
            }
        }
        closedir(dir);
        if (hits == 1)
        {
            snprintf(out, cap, "%s", found);
            char line[1300];
            snprintf(line, sizeof line, "引擎：游戏目录识别为 %s（手动解压套了一层，宿主自动认了）", out);
            host_log(line);
            return 0;
        }
        if (hits > 1)
        {
            host_log("引擎：Documents 里游戏目录候选不止一个（都带 RimWorld.dll）——只留一份再试");
            return -2;
        }
    }
    return -1;
}

// Appends dir/*.dll to the TPA list (':' separated). A name that also exists in skipDir is
// left out, so the game folder's framework copies do not shadow the bundle runtime's.
static void add_assemblies(char *list, size_t cap, const char *dir, const char *skipDir, int *count)
{
    DIR *d = opendir(dir);
    if (d == NULL)
    {
        char buf[512];
        snprintf(buf, sizeof buf, "引擎：列不了 %s：%s", dir, strerror(errno));
        host_log(buf);
        return;
    }
    struct dirent *e;
    while ((e = readdir(d)) != NULL)
    {
        size_t n = strlen(e->d_name);
        if (n < 5 || strcmp(e->d_name + n - 4, ".dll") != 0)
            continue;
        if (skipDir != NULL)
        {
            char other[1024];
            snprintf(other, sizeof other, "%s/%s", skipDir, e->d_name);
            if (file_exists(other))
                continue;
        }
        size_t len = strlen(list);
        snprintf(list + len, cap - len, "%s%s/%s", len > 0 ? ":" : "", dir, e->d_name);
        (*count)++;
    }
    closedir(d);
}

// The game's assemblies that say AMD64 in their PE machine field: pure IL, but the runtime
// refuses them on arm64. Android's Setup.MarkArm64 writes ARM64 there before the game runs;
// here the same two bytes are fixed in place, before the runtime sees them (idempotent —
// what is already 0xaa64, or I386 like most of the game's assemblies, is left alone).
static int mark_arm64_all(const char *game)
{
    DIR *d = opendir(game);
    if (d == NULL)
        return -1;
    int changed = 0;
    struct dirent *e;
    while ((e = readdir(d)) != NULL)
    {
        size_t n = strlen(e->d_name);
        if (n < 5 || strcmp(e->d_name + n - 4, ".dll") != 0)
            continue;
        char path[1100];
        snprintf(path, sizeof path, "%s/%s", game, e->d_name);
        FILE *f = fopen(path, "r+b");
        if (f == NULL)
            continue;
        unsigned char head[0x40];
        if (fread(head, 1, sizeof head, f) == sizeof head && head[0] == 'M' && head[1] == 'Z')
        {
            long pe = (long)head[0x3c] | ((long)head[0x3d] << 8) | ((long)head[0x3e] << 16) | ((long)head[0x3f] << 24);
            unsigned short machine = 0;
            if (pe > 0 && pe + 6 < 0x100000 && fseek(f, pe + 4, SEEK_SET) == 0 &&
                fread(&machine, 2, 1, f) == 1 && machine == 0x8664)
            {
                unsigned short arm64 = 0xaa64;
                if (fseek(f, pe + 4, SEEK_SET) == 0 && fwrite(&arm64, 2, 1, f) == 1)
                {
                    changed++;
                    char buf[1100];
                    snprintf(buf, sizeof buf, "引擎：%s 机器类型 AMD64 → ARM64", e->d_name);
                    host_log(buf);
                }
            }
        }
        fclose(f);
    }
    closedir(d);
    return changed;
}

// 眼睛：把一个目录里实际有什么摆进日志（找不到游戏时的自查——宿主看到的文件系统
// 和文件 app 里看到的到底差在哪，一次看清）。最多 max 项，装不下的以 … 收尾。
static void list_dir_into_log(const char *dir, int max)
{
    DIR *d = opendir(dir);
    if (d == NULL)
    {
        char line[1300];
        snprintf(line, sizeof line, "引擎：目录 %s 打不开（不存在？）", dir);
        host_log(line);
        return;
    }
    char line[1300];
    int shown = 0;
    struct dirent *e;
    snprintf(line, sizeof line, "引擎：%s 里有：", dir);
    size_t len = strlen(line);
    while ((e = readdir(d)) != NULL)
    {
        if (strcmp(e->d_name, ".") == 0 || strcmp(e->d_name, "..") == 0)
            continue;
        if (shown >= max)
        {
            snprintf(line + len, sizeof line - len, " …");
            break;
        }
        snprintf(line + len, sizeof line - len, "%s%s", shown > 0 ? "、" : "", e->d_name);
        len = strlen(line);
        shown++;
    }
    closedir(d);
    if (shown == 0)
        snprintf(line + len, sizeof line - len, "（空）");
    host_log(line);
}

int run_engine(const char *bundle_dir, const char *home)
{
    char game[1024], gameSlash[1024], tmp[1024], buf[1300];
    snprintf(tmp, sizeof tmp, "%s/tmp", home);

    // The game folder: <home>/Documents/game, or one wrapper deep (a hand-unpacked zip often
    // sits one level down), or a differently-named folder in Documents. No guesswork: when
    // nothing is found, the listings below show exactly what the process can see.
    game[0] = '\0';
    if (port17_find_game(home, game, sizeof game) != 0)
    {
        host_log("引擎：找不到游戏目录——宿主的视角如下（对照文件 app 里看到的排查）");
        char docs[1024];
        snprintf(docs, sizeof docs, "%s/Documents", home);
        list_dir_into_log(docs, 20);
        snprintf(buf, sizeof buf, "%s/game", docs);
        list_dir_into_log(buf, 20);
        host_log("引擎：期待 Documents/game/RimWorld.dll（PC 上 game 文件夹的内容，或丢一个 port17-game.zip 进 Documents 自动装）");
        return -1;
    }
    snprintf(gameSlash, sizeof gameSlash, "%s/", game);

    // What the engine cannot start without, in words the tester can act on.
    int missing = 0;
    static const char *const wanted[] = {
        "RimWorld.dll", "Assembly-CSharp.dll", "Assets.zip", "Data/Core", "Rendering/Shaders",
    };
    for (size_t i = 0; i < sizeof wanted / sizeof wanted[0]; i++)
    {
        snprintf(buf, sizeof buf, "%s/%s", game, wanted[i]);
        if (!file_exists(buf))
        {
            missing = 1;
            snprintf(buf, sizeof buf, "引擎：缺 %s（目录 %s）", wanted[i], game);
            host_log(buf);
        }
    }
    if (missing)
        return -1;

    // 改写戳（设备内 setup 写 port17-setup.stamp；PC 侧老流程写 os-detect+platform 组合）：
    // 5014 引擎的原生绑定只认 Windows/Linux/macOS，没有改写就跑会在 Vulkan 装载与 Platform
    // 家族两处硬墙给出明确报错。没有戳的目录本该在引擎模式前先过 setup——到这里还没戳
    // 多半是文件后来被替换过。
    {
        char stamp[1100], legacyA[1100], legacyB[1100];
        snprintf(stamp, sizeof stamp, "%s/port17-setup.v2.stamp", game);
        snprintf(legacyA, sizeof legacyA, "%s/os-detect.port17", game);
        snprintf(legacyB, sizeof legacyB, "%s/platform.port17", game);
        if (!file_exists(stamp) && !(file_exists(legacyA) && file_exists(legacyB)))
            host_log("引擎：没有改写戳（port17-setup.stamp）——这份游戏文件没被 setup 改写过；"
                     "重开 app 会让宿主自动跑设备内 setup");
    }

    redirect_stdio(home);
    host_log("==== 引擎启动（Waylith 程序集，官方 11.0 运行时） ====");

    mkdir(tmp, 0755);
    setenv("XDG_CONFIG_HOME", home, 1);
    setenv("XDG_DATA_HOME", home, 1);
    // The collector's youngest generation: the runtime sizes it by the processor's largest
    // cache, which iOS does not tell it either — the 32 MiB of Android's tuning (a 20 MB/s
    // allocator otherwise stops for a collection 80 times a second). runtime.env may say else.
    setenv("DOTNET_GCgen0size", "0x2000000", 1);
    // A memory fault in managed code stays an exception the game's own try/catch gets (the
    // PC runtime's WaylithClr.CatchableAccessViolations; the stock runtime would end instead).
    setenv("DOTNET_legacyCorruptedStateExceptionsPolicy", "1", 1);
    // Threads started without a size (the loading thread, the XML loader's): Darwin leaves
    // them 512 KB where the PC's exe gives 1.5 MB — 16 MB of address space, as the main thread.
    setenv("DOTNET_Thread_DefaultStackSize", "0x1000000", 1);
    // The interpreter line of M1: the official iOS pack is a JIT/interpreter mix and InterpMode=2
    // (disable R2R, run interpreted) is what made it start at all.
    setenv("COMPlus_InterpMode", "2", 1);
    setenv("COMPlus_EnableCrashReport", "1", 1);
    setenv("COMPlus_EnableDiagnostics", "1", 1);

    // <game>/runtime.env: NAME=value per line (a bare NAME= unsets), after the built-ins.
    {
        char path[1100];
        snprintf(path, sizeof path, "%s/runtime.env", game);
        const char *lines[32];
        static char storage[4096];
        int n = read_lines(path, lines, 32, storage, sizeof storage);
        for (int i = 0; i < n; i++)
        {
            char *eq = strchr((char *)lines[i], '=');
            if (eq == NULL || lines[i][0] == '#')
                continue;
            *eq = '\0';
            if (eq[1] != '\0')
                setenv(lines[i], eq + 1, 1);
            else
                unsetenv(lines[i]);
            snprintf(buf, sizeof buf, "引擎：环境 %s=%s", lines[i], eq + 1);
            host_log(buf);
        }
    }

    if (chdir(game) != 0)
    {
        snprintf(buf, sizeof buf, "引擎：chdir(%s) 失败：%s", game, strerror(errno));
        host_log(buf);
        return -2;
    }

    int fixed = mark_arm64_all(game);
    snprintf(buf, sizeof buf, "引擎：机器类型补写 %d 个程序集（AMD64→ARM64，安卓 Setup.MarkArm64 同款）", fixed);
    host_log(buf);

    static char tpa[1 << 20];
    tpa[0] = '\0';
    int count = 0;
    add_assemblies(tpa, sizeof tpa, bundle_dir, NULL, &count);
    int runtimeCount = count;
    add_assemblies(tpa, sizeof tpa, game, bundle_dir, &count);
    snprintf(buf, sizeof buf, "引擎：TPA 共 %d（运行时 %d + 游戏 %d）", count, runtimeCount, count - runtimeCount);
    host_log(buf);

    char nativeDirs[2200];
    snprintf(nativeDirs, sizeof nativeDirs, "%s:%s", bundle_dir, game);

    // The startup hook of M3 (PortHooks.dll in game/): the property is there the day the file is.
    char hooks[1100];
    snprintf(hooks, sizeof hooks, "%s/PortHooks.dll", game);
    if (!file_exists(hooks))
        hooks[0] = '\0';

    // Android's property group with two iOS facts: the RUNTIME_IDENTIFIER, and globalization —
    // the iOS runtime pack ships libSystem.Globalization.Native.dylib (it links the system's
    // libicucore), so ICU is tried as on the PC; runtime.properties can flip it to invariant.
    const char *allKeys[64] = {
        "STARTUP_HOOKS",
        "TRUSTED_PLATFORM_ASSEMBLIES",
        "APP_PATHS",
        "APP_NI_PATHS",
        "NATIVE_DLL_SEARCH_DIRECTORIES",
        "PLATFORM_RESOURCE_ROOTS",
        "APP_CONTEXT_BASE_DIRECTORY",
        "RUNTIME_IDENTIFIER",
        "System.Globalization.Invariant",
        "System.Globalization.PredefinedCulturesOnly",
        "System.GC.Server",
        "System.GC.Concurrent",
        "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization",
        "System.Reflection.Metadata.MetadataUpdater.IsSupported",
    };
    const char *allValues[64] = {
        hooks,
        tpa,
        game,
        game,
        nativeDirs,
        game,
        gameSlash,
        "ios-arm64",
        "false",
        "false",
        "false",
        "false",
        "true",
        "false",
    };
    int propertyCount = 14;

    // <game>/runtime.properties: extra properties, or new values for the defaults (the runtime
    // refuses a key given twice, so a default is replaced where it stands).
    {
        char path[1100];
        snprintf(path, sizeof path, "%s/runtime.properties", game);
        static char extraStorage[8192];
        const char *extraLines[32];
        int extraCount = read_lines(path, extraLines, 32, extraStorage, sizeof extraStorage);
        for (int i = 0; i < extraCount && propertyCount < 64; i++)
        {
            char *eq = strchr((char *)extraLines[i], '=');
            if (eq == NULL)
                continue;
            *eq = '\0';
            int at = propertyCount;
            for (int k = 0; k < propertyCount; k++)
            {
                if (strcmp(allKeys[k], extraLines[i]) == 0)
                {
                    at = k;
                    break;
                }
            }
            allKeys[at] = extraLines[i];
            allValues[at] = eq + 1;
            snprintf(buf, sizeof buf, "引擎：属性 %s=%s%s", extraLines[i], eq + 1,
                     at < propertyCount ? "（覆盖默认）" : "");
            host_log(buf);
            if (at == propertyCount)
                propertyCount++;
        }
    }

    char coreclrPath[1100];
    snprintf(coreclrPath, sizeof coreclrPath, "%s/libcoreclr.dylib", bundle_dir);
    void *lib = dlopen(coreclrPath, RTLD_NOW | RTLD_GLOBAL);
    if (lib == NULL)
    {
        host_log(dlerror());
        return -3;
    }
    coreclr_initialize_fn init = (coreclr_initialize_fn)dlsym(lib, "coreclr_initialize");
    coreclr_execute_assembly_fn execute = (coreclr_execute_assembly_fn)dlsym(lib, "coreclr_execute_assembly");
    coreclr_shutdown_fn shutdown = (coreclr_shutdown_fn)dlsym(lib, "coreclr_shutdown");
    coreclr_set_error_writer_fn set_err = (coreclr_set_error_writer_fn)dlsym(lib, "coreclr_set_error_writer");
    if (init == NULL || execute == NULL)
    {
        host_log("引擎：coreclr_initialize / coreclr_execute_assembly 缺失");
        return -4;
    }
    if (set_err != NULL)
        set_err(host_log); // the runtime's own words, or nothing at all

    char entry[1100];
    snprintf(entry, sizeof entry, "%s/RimWorld.dll", game);
    // exePath is the host executable (the M1 contract, verified with this runtime pack on
    // the device; Android gave the game dll here, which the full property set makes
    // equivalent — flipped here in one line if something path-shaped points the wrong way).
    char exePath[1100];
    snprintf(exePath, sizeof exePath, "%s/Port17Host", bundle_dir);
    void *host = NULL;
    unsigned int domain = 0;
    host_log("初始化 CoreCLR（解释器 + 32MiB gen0 + 16MB 线程栈）…");
    int hr = init(exePath, "port17-engine", propertyCount, allKeys, allValues, &host, &domain);
    if (hr != 0)
    {
        snprintf(buf, sizeof buf, "coreclr_initialize 失败：0x%08x", hr);
        host_log(buf);
        log_stderr_tail(home);
        return -5;
    }
    host_log("CoreCLR 就绪（引擎装载中——首屏前 stdout 滚动是正常进度）");

    const char *argv[32];
    static char argStorage[8192];
    char argsPath[1100];
    snprintf(argsPath, sizeof argsPath, "%s/args.txt", game);
    int argc = read_lines(argsPath, argv, 32, argStorage, sizeof argStorage);
    if (argc == 0)
    {
        argv[0] = "-NoCrashDialog";
        argc = 1;
    }
    for (int i = 0; i < argc; i++)
    {
        snprintf(buf, sizeof buf, "引擎：arg %d：%s", i, argv[i]);
        host_log(buf);
    }

    unsigned int exitCode = 0;
    hr = execute(host, domain, argc, (char **)argv, entry, &exitCode);
    if (hr != 0)
    {
        snprintf(buf, sizeof buf, "coreclr_execute_assembly 失败：0x%08x", hr);
        host_log(buf);
        log_stderr_tail(home);
        return -6;
    }
    snprintf(buf, sizeof buf, "引擎返回，退出码 %u", exitCode);
    host_log(buf);
    log_stderr_tail(home);
    if (shutdown != NULL)
        shutdown(host, domain);
    return (int)exitCode;
}
