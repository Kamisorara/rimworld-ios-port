// Stands in for steam_api64 on iOS (the Android port's host/stubs/steam_api64.c, silent):
// Steam is never running, every call says so. Steamworks.NET only goes on to the interfaces
// after SteamAPI_Init succeeds, so nothing beyond these is reached. Without the stub the
// launcher's SteamManager.InitIfNeeded spews a dlopen hunt into the log (and any unguarded
// call would throw DllNotFoundException).
#include <stdint.h>
#include <stddef.h>

#define EXPORT __attribute__((visibility("default")))

EXPORT int SteamAPI_RestartAppIfNecessary(uint32_t appId) { (void)appId; return 0; }
EXPORT int SteamAPI_Init(void) { return 0; }
EXPORT int SteamAPI_InitFlat(void *errorMessage) { (void)errorMessage; return 1; } // k_ESteamAPIInitResult_FailedGeneric
EXPORT int SteamAPI_InitSafe(void) { return 0; }
EXPORT void SteamAPI_Shutdown(void) {}
EXPORT void SteamAPI_RunCallbacks(void) {}
EXPORT int SteamAPI_IsSteamRunning(void) { return 0; }
EXPORT int32_t SteamAPI_GetHSteamUser(void) { return 0; }
EXPORT int32_t SteamAPI_GetHSteamPipe(void) { return 0; }
EXPORT const char *SteamAPI_GetSteamInstallPath(void) { return ""; }
EXPORT void SteamAPI_ReleaseCurrentThreadMemory(void) {}
EXPORT void SteamAPI_SetMiniDumpComment(const char *comment) { (void)comment; }
EXPORT void SteamAPI_WriteMiniDump(uint32_t a, void *b, uint32_t c) { (void)a; (void)b; (void)c; }
EXPORT void SteamInternal_SetMinidumpSteamID(uint64_t id) { (void)id; }
EXPORT void SteamAPI_RegisterCallback(void *callback, int type) { (void)callback; (void)type; }
EXPORT void SteamAPI_UnregisterCallback(void *callback) { (void)callback; }
EXPORT void SteamAPI_RegisterCallResult(void *callback, uint64_t call) { (void)callback; (void)call; }
EXPORT void SteamAPI_UnregisterCallResult(void *callback, uint64_t call) { (void)callback; (void)call; }
EXPORT void SteamAPI_ManualDispatch_Init(void) {}
EXPORT void SteamAPI_ManualDispatch_RunFrame(int32_t pipe) { (void)pipe; }
EXPORT int SteamAPI_ManualDispatch_GetNextCallback(int32_t pipe, void *msg) { (void)pipe; (void)msg; return 0; }
EXPORT void SteamAPI_ManualDispatch_FreeLastCallback(int32_t pipe) { (void)pipe; }
EXPORT void *SteamInternal_ContextInit(void *context) { (void)context; return NULL; }
EXPORT void *SteamInternal_CreateInterface(const char *name) { (void)name; return NULL; }
EXPORT void *SteamInternal_FindOrCreateUserInterface(int32_t user, const char *name) { (void)user; (void)name; return NULL; }
EXPORT void *SteamInternal_FindOrCreateGameServerInterface(int32_t user, const char *name) { (void)user; (void)name; return NULL; }
EXPORT void *SteamClient(void) { return NULL; }
EXPORT void *SteamInternal_SteamAPI_Init(const char *version, void *err) { (void)version; (void)err; return NULL; }
