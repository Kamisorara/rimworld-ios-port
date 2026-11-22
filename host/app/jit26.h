// The JIT26 arena acquisition, playport's ownership model: the HOST gets the
// memory, the runtime (patches/dualjit-arena.patch) only consumes pointers.
// Ported from the M1 probe host (probe/app/host.c) into the game host for the
// hybrid runtime line: JIT sessions launch through a StikDebug serve session
// (rw17-jit26.js), everything else runs interpreted and never calls in here.
#ifndef PORT17_JIT26_H
#define PORT17_JIT26_H

// The full in-process flow: live-debugger gate (CS_DEBUGGED + P_TRACED,
// fail-closed - an unserved brk is SIGTRAP death) -> up to 120s wait -> brk
// request (x0=NULL, x1=capacity; the script allocates RX via debugserver _M,
// blesses every page, returns the address in x0) -> vm_remap RW alias ->
// brk detach -> execution probe (unattached: faults are ours again) ->
// deep-page spot check -> PORT17_JIT_RX/RW/SIZE handoff env. Every step logs.
// Returns 1 when the runtime may start in JIT mode (env set), 0 otherwise.
int port17_jit26_acquire(void);

// csops CS_DEBUGGED (0x10000000): set when a debugger takes the task over,
// kept after detach (a sticky credential, not a live one - the live check is
// the P_TRACED half inside the acquire gate). 1 = set, 0 = clear, -1 unknown.
int port17_jit26_cs_debugged(void);

#endif
