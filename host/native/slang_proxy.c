// libslang.so for the phone, without code of its own: it names libslang-compiler.so (Slang 2026.19, the PC game's
// shader compiler, built for Android by native/slang/build-official.cmd) as a needed library, and dlsym on a library
// searches the libraries it needs as well, so the engine's P/Invokes into "slang" (Brutal.Slang.dll) land in Slang.
// Slang's own Linux and Android builds make libslang.so a symbolic link to libslang-compiler.so, which an APK cannot
// hold (built with -Wl,--no-as-needed, so that the link stays although nothing here uses it).
