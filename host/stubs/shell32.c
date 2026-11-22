// Stands in for the Windows shell32 on iOS. The launcher's Bootstrap.ResolvePersistentDataRoot
// asks it where LocalLow is (SHGetKnownFolderPath) when neither the OSX nor the Linux branch
// took the iOS runtime — the call is the Windows fallback. A failure return (non-zero) is what
// the managed side plans for: it takes its own fallback path (UserProfile/AppData/LocalLow),
// which lands in the app container, writable. Named exactly "libshell32.dll" — the dlopen the
// runtime makes under that name (it does not strip ".dll" on Darwin), so the bundle file has
// to carry it verbatim.
#include <stddef.h>

#define EXPORT __attribute__((visibility("default")))

EXPORT int SHGetKnownFolderPath(const void *folderId, unsigned int flags,
                                const void *token, void **path)
{
    (void)folderId;
    (void)flags;
    (void)token;
    (void)path; // not set on failure, the caller only reads it on success
    return 1;   // any non-zero: the managed side takes its own fallback
}
