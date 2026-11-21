// The M2 native-library load check: every engine dylib the app carries, dlopen'ed and one known
// symbol resolved from each — the cheap on-device proof that the bundle's dylibs load (their
// install names, their dependencies) before the engine itself comes and needs them all at once.
#include <dlfcn.h>
#include <mach-o/dyld.h>
#include <stdint.h>
#include <stdio.h>
#include <unistd.h>

void host_log(const char *msg);

int libs_check(void)
{
    static const struct
    {
        const char *lib;
        const char *sym;
    } checks[] = {
        { "libglfw.dylib", "glfwIosAttach" },
        { "libvulkan.dylib", "vkGetInstanceProcAddr" },
        { "MoltenVK.framework/MoltenVK", "vkGetInstanceProcAddr" },
        { "libwaylithaudio.dylib", "wa_init" },
        { "libvma.dylib", "vmaCreateAllocator" },
        { "libstb.dylib", "stbi_load" },
        { "libgli.dylib", "createEmpty" },
        { "libKtx.dylib", "ktxErrorString1" },
        { "libportastc.dylib", "port_astc_encode" },
        { "libslang.dylib", "slang_createGlobalSession" },
        { "libslang-compiler.dylib", "slang_createGlobalSession" },
        // Slang 的两个伴生库是它自己 dlopen 的（内部 C++ 符号，无稳定入口）：装载成功即算过。
        { "libslang-glslang-2026.19.dylib", NULL },
        { "libslang-glsl-module-2026.19.dylib", NULL },
    };
    char path[1024];
    uint32_t n = sizeof path - 1;
    _NSGetExecutablePath(path, &n);
    char *slash = path;
    for (char *p = path; *p; p++) if (*p == '/') slash = p; // dirname
    int ok = 0;
    int total = (int)(sizeof checks / sizeof checks[0]);
    for (int i = 0; i < total; i++)
    {
        char full[1200];
        snprintf(full, sizeof full, "%.*s/%s", (int)(slash - path), path, checks[i].lib);
        void *lib = dlopen(full, RTLD_NOW | RTLD_LOCAL);
        if (!lib)
        {
            char line[1400];
            snprintf(line, sizeof line, "库自检：%s 装载失败：%s", checks[i].lib, dlerror());
            host_log(line);
            continue;
        }
        void *sym = checks[i].sym ? dlsym(lib, checks[i].sym) : (void *)lib;
        char line[1400];
        if (checks[i].sym)
            snprintf(line, sizeof line, "库自检：%-36s %s（%s）", checks[i].lib,
                     sym ? "通过" : "符号缺失！", checks[i].sym);
        else
            snprintf(line, sizeof line, "库自检：%-36s %s（仅装载）", checks[i].lib,
                     sym ? "通过" : "异常");
        host_log(line);
        if (sym) ok++;
    }
    char done[96];
    snprintf(done, sizeof done, "库自检：%d/%d 通过", ok, total);
    host_log(done);
    return ok == total ? 0 : 1;
}
