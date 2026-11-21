// libslang.dylib for the device, without code of its own: it names libslang-compiler.dylib (Slang 2026.19,
// the PC game's shader compiler, built for iOS by host/build-slang.sh) as a needed library, and dlsym on a
// library searches the libraries it needs as well, so the engine's P/Invokes into "slang" (Brutal.Slang.dll)
// land in Slang. Slang's own Linux and Android builds make libslang a symbolic link to libslang-compiler,
// which a bundle cannot hold.
//
// On Darwin the link stays without tricks of the linker: the anchor below takes the address of one of
// Slang's entry points, which leaves an undefined symbol for the linker to resolve against
// libslang-compiler.dylib (-lslang-compiler) — the dependency becomes a load command even though nothing
// here calls it. (ld64's -needed_library does not consult the -L search paths, so the Android way
// (-Wl,--no-as-needed) has no direct equivalent here.)
extern int slang_createGlobalSession(void **outSession);

__attribute__((visibility("default"))) int port17_slang_anchor(void)
{
    return (int)(long)(void *)&slang_createGlobalSession;
}
