// Port17 JIT session script for the OFFICIAL Microsoft ios-arm64 runtime
// (M3 JIT line; the rw17 arena machinery of rw17-jit.js is NOT needed here —
// that was for the archived self-built runtime. The official build allocates
// its own JIT code pages via mmap/mprotect; the ONLY thing iOS requires is
// the process being debugged (CS_DEBUGGED), which StikDebug's spawn grants
// and this session keeps granted for the app's lifetime.)
//
//   1. attach the session pid (the app StikDebug spawned for this session);
//   2. resume it ('c') — the app then runs: engine mode, CoreCLR, native JIT;
//   3. hold the session open (universal.js model) while you play.
//
// The app must have <game>/jit.on (or the runtime.env InterpMode=0 lines) so
// the runtime actually uses the JIT; launching Port17 normally from the home
// screen WITHOUT StikDebug while jit.on is present may fail at startup
// (JIT-only config, no authorization) — always start it through this session.
//   port17-ui.log's "生效执行模式" line should say JIT; world load and colony
//   simulation should be dramatically faster than the interpreter baseline.
(() => {
    const pid = get_pid();
    log("port17-jit: session pid = " + pid + " (must be Port17)");
    const stop = send_command("vAttach;" + pid.toString(16));
    if (!/^[TS][0-9a-f]{2}/i.test(stop)) throw Error("Attach failed: " + stop);
    log("port17-jit: attached — JIT authorized (CS_DEBUGGED), resuming the app");
    try {
        // vAttach stopped every thread; ALWAYS put the process back before
        // anything else (a stopped process = black-screen-no-logs).
        send_command("c");
    } catch (e) {
        log("port17-jit: initial c: " + e);
    }
    log("port17-jit: holding the session open — KEEP THIS WINDOW OPEN while playing");
    for (;;) {
        const resp = send_command("c");
        if (!/^[TS][0-9a-f]{2}/i.test(resp)) { log("port17-jit: session ended: " + resp); break; }
        log("port17-jit: process stopped (" + resp.substring(0, 20) + "), continuing");
    }
})();
